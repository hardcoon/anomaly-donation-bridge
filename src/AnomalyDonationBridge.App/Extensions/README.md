# Declarative extension manifest v1

Anomaly Donation Bridge reads extension manifests from
`%LocalAppData%\AnomalyDonationBridge\extensions`. The loader only reads
top-level files named `*.adb-extension.json`; it never loads assemblies or
executes .NET code from this directory.

An extension adds editor metadata for Lua handlers supplied by a modpack. The
game addon must register each handler separately and advertise its exact
`handlerId` through the live `CAPABILITIES` message. A manifest cannot make an
unsupported game handler executable.

```json
{
  "schemaVersion": 1,
  "protocolVersion": "1",
  "extensionId": "vendor.modpack",
  "displayName": "Vendor Modpack",
  "actions": [
    {
      "handlerId": "vendor.modpack.spawn_companion",
      "displayName": "Создать спутника",
      "description": "Пример декларативного действия.",
      "scriptRelativePath": "gamedata/scripts/vendor_modpack_spawn_companion.script",
      "cooldownSeconds": 10,
      "parameters": [
        {
          "key": "section_id",
          "displayName": "Профиль NPC",
          "kind": "ItemChoice",
          "catalogKind": "Npc",
          "defaultValue": ""
        },
        {
          "key": "count",
          "displayName": "Количество",
          "kind": "Integer",
          "minimum": 1,
          "maximum": 5,
          "options": [],
          "defaultValue": "1"
        }
      ]
    }
  ]
}
```

## Validation rules

- `schemaVersion` must be `1`; `protocolVersion` must be the string `"1"`.
- A single manifest may structurally declare from 1 to 100 actions. The launcher
  processes manifests in deterministic sorted-path order and accepts no more
  than 64 extension actions across the whole directory. A manifest that does
  not fit the remaining capacity is rejected transactionally; later manifests
  are still considered. The public Lua extension registry uses the same
  aggregate limit of 64 handlers.
- `extensionId` is a lowercase namespaced identifier with at least two
  segments, for example `vendor.modpack`.
- Every `handlerId` must begin with `<extensionId>.` and add at least one
  non-empty lowercase segment. Each segment starts with `a-z`, is at most 32
  characters, and may then contain `a-z`, `0-9`, `_`, or `-`; the complete ID
  is at most 128 characters. Built-in handler IDs and handler IDs from another
  accepted manifest cannot be overridden.
- `scriptRelativePath` is metadata only. It must point to a namespaced `.script`
  directly inside `gamedata/scripts`; its filename must begin with the
  underscore-normalized extension ID, for example `vendor_modpack_`.
- `kind` is one of `Integer`, `Decimal`, `Percent`, `Choice`, `ItemChoice`, or
  `MultiChoice`. `catalogKind` is optional and is one of `Mutant`, `Npc`,
  `StalkerSquad`, `MutantSquad`, `Item`, or `Anomaly`.
- A parameter with `catalogKind` must use `Choice`, `ItemChoice`, or
  `MultiChoice`. Its options come only from the current safe game catalog; the
  cached catalog remains read-only. The current catalog revision is attached
  automatically and checked again before IPC dispatch.
- `catalogCategories` may optionally restrict a dynamic parameter to named
  catalog categories. The filter is internal: the editor shows only matching
  entries and does not add another user-facing control.
- A non-catalog choice requires a non-empty `options` array. Numeric parameters
  use `minimum` and `maximum` when applicable.
- `catalog_revision` is reserved by the IPC protocol and cannot be declared.
- A malformed manifest is rejected transactionally: none of its actions are
  loaded. Other valid manifests remain available, and the UI shows the file and
  validation error.

The loader intentionally has no escape hatch for quarantine entries and no
facility for arbitrary managed-code plugins.
