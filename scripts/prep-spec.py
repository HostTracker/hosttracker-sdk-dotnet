#!/usr/bin/env python3
"""Prepare the published HostTracker OpenAPI document for the .NET generator.

Every transform is generator ergonomics; none of them changes the contract.

1. `enum` is stripped from every schema. The document's open vocabularies (`x-extensible-enum`)
   grow by design, and a C# enum cannot hold a value it was not compiled with. The closed sets go
   too, because NSwag's System.Text.Json output does not round-trip them: it names each member
   after its value (`monitor.down` -> `Monitor_down`) and spells the wire value only in
   `[EnumMember]`, which `JsonStringEnumConverter` ignores on net8. Such a client writes
   `"VoiceCall"` for `voiceCall` and throws reading `"monitor.down"`. The vocabularies survive as
   constant classes (see `--vocab-out`).

2. Pure `oneOf` unions are flattened into one open object. NJsonSchema emits an empty class for a
   schema that is only `oneOf` + `discriminator`, then invents an anonymous type it never writes.
   Merging the branches gives one class per union with every branch member optional.

3. `format: uri` is dropped. NSwag maps it to `System.Uri`, and System.Text.Json fails the whole
   payload on a value it cannot parse - including the empty string this API uses for a cleared
   field. That would turn a cosmetic server value into a hard client error, on `Problem.type` and
   its per-code shapes most of all: the error path is the worst place to lose a response. The
   members that do hold absolute urls (a webhook's `url`, the status-page urls) read like every
   other string member this way.

4. `Idempotency-Key` header parameters become optional. NSwag turns a required header into a
   required method argument that throws on null, which would defeat the client's own auto-key
   handler. The server still requires the header.

5. Inline object schemas are hoisted to derived names. A nested object written inline has no name
   of its own, so the generator invents one from the property alone (`Locations`, `Settings2`);
   hoisting names it after its parent. The published document names its own components, so this is
   down to a handful of leftovers.

The document arrives free of `$ref` siblings and `anyOf` null wrappers - the publisher's own
down-converter resolves both - and its tags are already the single PascalCase words NSwag needs to
derive `MonitorTypesClient` from `MonitorTypes`.

The script also emits the page-envelope adapters (`--pages-out`) that let `Pagination` walk any
generated `*Page` type without reflection.

Usage: prep-spec.py <in.json> <out.json> [--vocab-out <f.cs>] [--pages-out <f.cs>]
"""
import json
import re
import sys

# (C# class name, doc summary, JSON pointer into the original document).
# A missing pointer is a hard failure rather than a silently empty vocabulary.
VOCABULARIES = [
    ("MonitorTypes", "The 14 monitor type tokens (`MonitorView.type`).",
     "/components/schemas/MonitorView/properties/type/enum"),
    ("MonitorStates", "A monitor's lifecycle state (`MonitorView.state`).",
     "/components/schemas/MonitorView/properties/state/enum"),
    ("ContactTypes", "The contact channel tokens (`ContactView.type`).",
     "/components/schemas/ContactView/properties/type/enum"),
    ("IncidentStates", "An incident's state (`IncidentView.state`).",
     "/components/schemas/IncidentView/properties/state/enum"),
    ("JobStates", "A job's lifecycle state (`JobView.state`). Terminal: succeeded, partial, failed, cancelled.",
     "/components/schemas/JobView/properties/state/enum"),
    ("JobItemStatuses", "What a job did with one item (`JobItemView.status`).",
     "/components/schemas/JobItemView/properties/status/enum"),
    ("InstantCheckStates", "An instant check's state (`IcResultView.state`).",
     "/components/schemas/IcResultView/properties/state/enum"),
    ("WebhookEvents", "The webhook event types (`WebhookEnvelope` discriminator).",
     "/components/schemas/WebhookEnvelope/discriminator/mapping"),
]


def pascal(text: str) -> str:
    parts = [p for p in re.split(r"[^A-Za-z0-9]+", text) if p]
    return "".join(p[:1].upper() + p[1:] for p in parts)


def flatten_unions(doc) -> int:
    """A schema that is only `oneOf` + `discriminator` becomes one object carrying the union of its
    branches' properties: identical definitions are kept, differing ones widened to their common
    `type` (or left untyped). Only the members every branch requires stay required."""
    schemas = (doc.get("components") or {}).get("schemas") or {}
    flattened = 0
    for name, sc in schemas.items():
        if not isinstance(sc, dict) or "oneOf" not in sc or "properties" in sc:
            continue
        branches = []
        for member in sc["oneOf"]:
            ref = member.get("$ref") if isinstance(member, dict) else None
            target = schemas.get(ref.split("/")[-1]) if ref else member
            if isinstance(target, dict):
                branches.append(target)
        if not branches:
            continue

        merged = {}
        required = None
        for branch in branches:
            for prop, definition in (branch.get("properties") or {}).items():
                if prop not in merged:
                    merged[prop] = json.loads(json.dumps(definition))
                elif merged[prop] != definition:
                    merged[prop] = widen(merged[prop], definition)
            names = set(branch.get("required") or [])
            required = names if required is None else (required & names)

        description = sc.get("description")
        sc.clear()
        sc["type"] = "object"
        if description:
            sc["description"] = description
        sc["properties"] = merged
        if required:
            sc["required"] = sorted(required)
        flattened += 1
    return flattened


def widen(a: dict, b: dict) -> dict:
    """The loosest schema that accepts both - same `type` when they agree, untyped otherwise."""
    out = {}
    description = a.get("description") or b.get("description")
    if description:
        out["description"] = description
    if "$ref" not in a and "$ref" not in b and a.get("type") and a.get("type") == b.get("type"):
        out["type"] = a["type"]
        if a.get("format") and a.get("format") == b.get("format"):
            out["format"] = a["format"]
        if a.get("type") == "array" and a.get("items") == b.get("items") and a.get("items"):
            out["items"] = a["items"]
    return out


def relax_idempotency_key(doc) -> int:
    """`Idempotency-Key` header parameters become optional - the client's handler supplies one."""
    n = 0
    methods = ("get", "put", "post", "delete", "options", "head", "patch", "trace")
    for item in (doc.get("paths") or {}).values():
        for method, op in item.items():
            if method not in methods or not isinstance(op, dict):
                continue
            for param in op.get("parameters") or []:
                if (isinstance(param, dict) and param.get("in") == "header"
                        and param.get("name") == "Idempotency-Key" and param.get("required")):
                    param["required"] = False
                    n += 1
    return n


def singular(name: str) -> str:
    if name.endswith("ies") and len(name) > 3:
        return name[:-3] + "y"
    if name.endswith("ss") or not name.endswith("s"):
        return name
    return name[:-1]


def is_inline_object(schema) -> bool:
    return (isinstance(schema, dict) and "$ref" not in schema
            and isinstance(schema.get("properties"), dict) and bool(schema["properties"]))


def hoist_inline_objects(doc) -> int:
    """Give every inline nested object a name derived from where it lives, so the generator does not
    have to invent one from the property alone."""
    schemas = (doc.get("components") or {}).get("schemas")
    if not isinstance(schemas, dict):
        return 0
    hoisted = 0

    def unique(name: str) -> str:
        if name not in schemas:
            return name
        n = 2
        while f"{name}{n}" in schemas:
            n += 1
        return f"{name}{n}"

    def visit(node, owner: str):
        nonlocal hoisted
        if not isinstance(node, dict):
            return
        for key in ("allOf", "anyOf", "oneOf"):
            for member in node.get(key) or []:
                visit(member, owner)
        props = node.get("properties")
        if not isinstance(props, dict):
            return
        for prop in sorted(props):
            schema = props[prop]
            if not isinstance(schema, dict):
                continue
            if is_inline_object(schema):
                name = unique(owner + pascal(prop))
                schemas[name] = schema
                visit(schema, name)
                props[prop] = {"$ref": "#/components/schemas/" + name}
                hoisted += 1
            elif schema.get("type") == "array" and is_inline_object(schema.get("items")):
                name = unique(owner + pascal(singular(prop)))
                schemas[name] = schema["items"]
                visit(schema["items"], name)
                schema["items"] = {"$ref": "#/components/schemas/" + name}
                hoisted += 1
            else:
                visit(schema, owner)

    for name in sorted(schemas):
        visit(schemas[name], name)
    return hoisted


def drop_uri_format(node) -> int:
    """`{"type": "string", "format": "uri"}` -> `{"type": "string"}`."""
    n = 0
    if isinstance(node, dict):
        if node.get("type") == "string" and node.get("format") == "uri":
            del node["format"]
            n += 1
        for v in node.values():
            n += drop_uri_format(v)
    elif isinstance(node, list):
        for v in node:
            n += drop_uri_format(v)
    return n


def infer_type(values):
    if all(isinstance(v, bool) for v in values):
        return "boolean"
    if all(isinstance(v, int) and not isinstance(v, bool) for v in values):
        return "integer"
    if all(isinstance(v, str) for v in values):
        return "string"
    return None


def strip_enums(node) -> int:
    n = 0
    if isinstance(node, dict):
        vals = node.get("enum")
        if isinstance(vals, list):
            node.pop("enum", None)
            node.pop("x-enumNames", None)
            node.pop("x-enum-varnames", None)
            if "type" not in node and "$ref" not in node:
                t = infer_type(vals)
                if t:
                    node["type"] = t
            n += 1
        for v in node.values():
            n += strip_enums(v)
    elif isinstance(node, list):
        for v in node:
            n += strip_enums(v)
    return n


def resolve(doc, pointer):
    node = doc
    for seg in pointer.strip("/").split("/"):
        seg = seg.replace("~1", "/").replace("~0", "~")
        if not isinstance(node, dict) or seg not in node:
            return None
        node = node[seg]
    return node


def member_name(value: str) -> str:
    parts = [p for p in re.split(r"[^A-Za-z0-9]+", value) if p]
    name = "".join(p[:1].upper() + p[1:] for p in parts)
    if not name or name[0].isdigit():
        name = "_" + name
    return name


def emit_vocabularies(doc, path):
    out = [
        "//----------------------",
        "// <auto-generated>",
        "//     Generated by scripts/prep-spec.py from the published OpenAPI document.",
        "//     The wire types are plain strings (open vocabularies); these constants",
        "//     are the values the document knows about at generation time. A value the",
        "//     server adds later arrives as a plain string and never breaks the client.",
        "// </auto-generated>",
        "//----------------------",
        "",
        "#nullable enable",
        "",
        "namespace HostTracker.Sdk",
        "{",
    ]
    for cls, summary, pointer in VOCABULARIES:
        node = resolve(doc, pointer)
        if node is None:
            raise SystemExit(
                f"prep-spec: vocabulary pointer '{pointer}' for {cls} is gone from the spec. "
                f"Fix VOCABULARIES in scripts/prep-spec.py.")
        values = list(node.keys()) if isinstance(node, dict) else list(node)
        values = [v for v in values if isinstance(v, str) and v != ""]
        out += [
            "    /// <summary>",
            f"    /// {summary}",
            "    /// </summary>",
            "    public static class " + cls,
            "    {",
        ]
        for v in values:
            out.append(f"        /// <summary><c>{v}</c></summary>")
            out.append(f"        public const string {member_name(v)} = \"{v}\";")
            out.append("")
        out.append("        /// <summary>Every value this vocabulary published at generation time.</summary>")
        out.append("        public static readonly System.Collections.Generic.IReadOnlyList<string> All = new[]")
        out.append("        {")
        for v in values:
            out.append(f"            {member_name(v)},")
        out.append("        };")
        out.append("    }")
        out.append("")
    out.append("}")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out).rstrip() + "\n")
    return sum(1 for _ in VOCABULARIES)


def emit_page_adapters(doc, path):
    """Every collection envelope (`{data, nextCursor, hasMore}`) gets a partial
    declaration implementing IPageEnvelope<T>, so the paging helpers can walk it
    with no reflection and no per-call adapter lambda."""
    schemas = (doc.get("components") or {}).get("schemas") or {}
    rows = []
    for name, sc in schemas.items():
        if not isinstance(sc, dict):
            continue
        props = sc.get("properties") or {}
        if not all(k in props for k in ("data", "nextCursor", "hasMore")):
            continue
        ref = ((props["data"].get("items") or {}).get("$ref") or "")
        if not ref:
            continue
        rows.append((name, ref.split("/")[-1], "syncCursor" in props))
    rows.sort()
    out = [
        "//----------------------",
        "// <auto-generated>",
        "//     Generated by scripts/prep-spec.py from the published OpenAPI document.",
        "//     One partial per collection envelope, wiring it to IPageEnvelope<T> so the",
        "//     paging helpers can walk any list endpoint uniformly.",
        "// </auto-generated>",
        "//----------------------",
        "",
        "#nullable enable",
        "",
        "namespace HostTracker.Sdk.Generated",
        "{",
        "    using System.Collections.Generic;",
        "    using HostTracker.Sdk;",
        "",
    ]
    for name, item, has_sync in rows:
        iface = f"IPageEnvelope<{item}>"
        out += [
            f"    public partial class {name} : {iface}",
            "    {",
            f"        IReadOnlyList<{item}> {iface}.Items => "
            f"PageEnvelope.AsList(Data);",
            f"        string? {iface}.Cursor => NextCursor;",
            f"        bool {iface}.More => HasMore;",
            f"        string? {iface}.Sync => " + ("SyncCursor;" if has_sync else "null;"),
            f"        PageCount? {iface}.Counts => " + ("Count;" if "count" in ((schemas[name].get("properties") or {})) else "null;"),
            "    }",
            "",
        ]
    out.append("}")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out).rstrip() + "\n")
    return len(rows)


def main(argv):
    if len(argv) < 3:
        raise SystemExit(__doc__)
    src, dst = argv[1], argv[2]
    vocab_out = None
    if "--vocab-out" in argv:
        vocab_out = argv[argv.index("--vocab-out") + 1]
    pages_out = None
    if "--pages-out" in argv:
        pages_out = argv[argv.index("--pages-out") + 1]

    with open(src, encoding="utf-8") as f:
        doc = json.load(f)

    # The vocabularies come from the original document: the enums and the webhook discriminator
    # that the transforms below are about to open up.
    if vocab_out:
        count = emit_vocabularies(doc, vocab_out)
        print(f"prep-spec: wrote {count} vocabulary classes to {vocab_out}")

    unions = flatten_unions(doc)
    hoisted = hoist_inline_objects(doc)
    keys = relax_idempotency_key(doc)
    uris = drop_uri_format(doc)
    stripped = strip_enums(doc)

    # The page adapters come from the transformed document, so any envelope the transforms above
    # renamed or hoisted gets one too.
    if pages_out:
        count = emit_page_adapters(doc, pages_out)
        print(f"prep-spec: wrote {count} page adapters to {pages_out}")

    with open(dst, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    print(f"prep-spec: {len(doc.get('paths', {}))} paths; "
          f"{stripped} enum schemas opened; {unions} oneOf unions flattened; "
          f"{uris} uri formats relaxed; {keys} idempotency-key params relaxed; "
          f"{hoisted} inline objects hoisted; wrote {dst}")


if __name__ == "__main__":
    main(sys.argv)
