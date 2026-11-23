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

AutoCQ computes manual goals from the main logbook and presents HAMLOG issuance separately. For the two built-in templates, a catalog row with the matching page URL and empty goals uses the published dates and built-in rules. Existing manually entered goals take precedence. A **Check HAMLOG** result belongs to the local operator profile and is preserved when the matching catalog entry is refreshed.

Murom Falcon uses 85 credited points. A new local contact is worth 6 or 10 points before multipliers. The calculation uses the public club roster checked on 2026-11-23. Same-day repeats are grouped by member, band and mode group. Confirmed contacts come from the public callsign search. Local contacts awaiting credit are shown separately. [Award rules](https://hamlog.online/club/nebo/dc4b742d6f1c2c631f11703d0565cacf/rules/) · [Club roster](https://www.skyham.ru/spisok-kluba.htm)

The Tula 875 template shows credit for the historical September 2021 period. [Award rules](https://hamlog.online/global/russia/r3p/a8052398c5047a367fe59da599b1818f/rules/)
