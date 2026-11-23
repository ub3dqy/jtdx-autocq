# Award catalog import

AutoCQ reads a UTF-8 JSON file through **«Журнал и дипломы → Дипломы HAMLOG → Импорт»**. A catalog export can list programs and issuance without providing targets. The operator can enter the targets in the award editor.

```json
{
  "version": 1,
  "callsign": "K1ABC",
  "generatedUtc": "2026-01-15T12:00:00Z",
  "programs": [
    {
      "id": "example-20m-2026",
      "title": "Demo award for 20 metres",
      "url": "",
      "fromUtc": "2026-01-01",
      "untilUtc": "2026-12-31",
      "hasIssued": false,
      "goals": [
        { "kind": "call", "value": "JA1ABC", "band": "20m", "mode": "FT8" },
        { "kind": "dxcc", "value": "PY", "band": "20m" },
        { "kind": "mask", "value": "VK*", "count": 2 }
      ]
    }
  ]
}
```

- Supply `version` and `programs`. `callsign` is optional for shared rules without personal issuance data. A personal catalog can be used for its matching operator callsign.
- Supply a `title` and a stable `id` or HAMLOG award page `url`. For an omitted `id`, AutoCQ uses the normalized page URL when available. An import updates matching entries and retains previously entered goals when a catalog row contains no goals. A refresh with an older `generatedUtc` is rejected.
- Set `hasIssued` to true when HAMLOG lists an issued award for that program. Use null or omit the field when issuance is unknown. The editor lets an operator search for a different level in an issued program.
- Use `call` for an exact callsign, `dxcc` for the primary prefix from JTDX's DXCC table and `mask` for a pattern with `*` or `?`. `count` defaults to one and is always one for a specific callsign. A mask or country can require up to 1,000 distinct full callsigns. All goals of a program must be met.
- Leave `band` and `mode` blank to accept any recorded band or mode. For an explicit mode, use `FT8` or `FT4`. Automatic searches run in the band and digital mode currently selected in JTDX.
- Date-only bounds use UTC and include the final day. An exact timestamp ending in `Z` can be used for either bound. An exact end timestamp is exclusive.

The application computes progress from the main logbook and presents HAMLOG issuance separately. It stores imported catalogs locally. The operator chooses which programs to use for monitoring.
