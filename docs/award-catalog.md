# Award catalog and search rules

**«Обновить каталог»** (Update catalog) loads the public HAMLOG award list and published conditions. **«Полученные из кабинета…»** (Received awards from my account) reads saved pages from **My received awards**. Save every page as HTML and select the files together. AutoCQ uses the issuance records for the selected callsign to make those awards unavailable for search.

Use **«Импортировать список из файла…»** (Import list from file) in the table's right-click menu to load a UTF-8 JSON file. For a standalone search list, leave `url` blank and add criteria to `goals`.

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
        { "kind": "mask", "value": "VK*" }
      ]
    }
  ]
}
```

- Supply `version` and `programs`. Use `callsign` for a personal export. Shared search rules can omit it.
- Supply a `title` and either a stable `id` or the HAMLOG award page `url`. An import updates matching entries. If the file supplies no targets for an existing entry, AutoCQ keeps the saved targets. An import with an older `generatedUtc` is rejected.
- Set `hasIssued` to true when the award appears in **My received awards**. Use null or omit the field when issuance is unknown.
- Use `call` for an exact callsign, `dxcc` for the entity's main prefix in the JTDX country file, or `mask` for a pattern with `*` or `?`. A live station needs to match one of the criteria. Older files can still contain `count`.
- An empty `band` or `mode` accepts the current JTDX band or mode. Set `FT8` or `FT4` to restrict the mode. Date-only bounds use UTC and include the final day. Exact end timestamps are exclusive.
- Use `manualSearch: true` with your targets for an award URL you want to configure locally. Published dates still apply when available.

An award review records the public HAMLOG page and the SHA-256 digest of the normalized rule text. For an image-only document, it can identify the exact PDF. A review supplies search criteria or explains why AutoCQ cannot identify eligible stations from the information decoded in JTDX. Edits saved by the operator take precedence on catalog refresh. [Reviewed award records](../src/HamlogReviewedAwards.json)

Station criteria can include listed callsigns, DXCC entities, clubs, regions, date and time, bands, modes and locators. For nationwide RDA awards the search covers UA, UA9 and UA2. Regional awards use the callsign series of the relevant region. A district within a region is searched by region. **My Russia** uses a fresh grid locator, and **Digital QSO Long Distance** compares the two stations' locators. [SRR subject table](https://srr.ru/spisok-subektov-rossijskoj-federats/) · [RCPW roster](https://rcpw.ru/members.html) · [My Russia rules](https://hamlog.online/rrnars/rules.pdf#page=27)

Use your HAMLOG account for credited contacts, repeats, points and awards.
