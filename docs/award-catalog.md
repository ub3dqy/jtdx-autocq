# Award catalog import

AutoCQ reads a UTF-8 JSON file through **«Импортировать список из файла…»** (Import list from file) in the award table's right-click menu. **«Обновить каталог»** (Update catalog) loads the public HAMLOG catalog and its published conditions. **«Выполненные из кабинета…»** reads a saved HAMLOG awards page or a matching AutoCQ JSON export. The saved HTML supplies confirmed issuance records for the selected callsign. For a standalone search list, leave `url` blank and include your own targets in `goals`.

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
- For an award URL without built-in rules, set `manualSearch` to true to use the supplied `goals` as a local search list. This is the same as saving **My targets** in the dialog. Legacy files with a URL and goals alone still open for reading. Published dates constrain the local search window when available.

AutoCQ computes manual goals from the main logbook and presents HAMLOG issuance separately. For the supported award URLs under **Built-in awards**, AutoCQ looks up the operator's credit before starting a search. It applies the built-in rules even if an older catalog included general callsign masks or DXCC goals. A **Check HAMLOG** result belongs to the local operator profile and is preserved when the matching catalog entry is refreshed. The **Award rules** button reads published HTML or offers the official PDF.

My Russia uses the HAMLOG Hunter square map to find the next tier and checks the first four characters of a fresh grid locator. For nationwide RDA awards AutoCQ selects Russian DXCC entities UA, UA9 and UA2. For regional awards it selects the callsign series assigned to the relevant region. The logbook reserves repeats by callsign, band and mode group. HAMLOG supplies the credit and awards. [My Russia rules](https://hamlog.online/rrnars/rules.pdf#page=27) · [Legendary Gunsmiths of Tula rules](https://hamlog.online/global/russia/r3p/eb739f05ee1eb35b118a447552a8d0c2/rules/)

Murom Falcon uses 85 credited points. A new local contact is worth 6 or 10 points before multipliers. The calculation uses the public club roster checked on 2026-11-23. Same-day repeats are grouped by member, band and mode group. Confirmed contacts come from the public callsign search. Local contacts awaiting credit are shown separately. [Award rules](https://hamlog.online/club/nebo/dc4b742d6f1c2c631f11703d0565cacf/rules/) · [Club roster](https://www.skyham.ru/spisok-kluba.htm)

The Tula 875 template shows credit for the historical September 2021 period. [Award rules](https://hamlog.online/global/russia/r3p/a8052398c5047a367fe59da599b1818f/rules/)

The document parser recognizes more than one scoring group in the supported Knights of the Sky conditions. Named calls take their applicable score. Regional and country groups use the current DXCC lookup or regular Russian call series from the SRR subject table. [SRR subject table](https://srr.ru/spisok-subektov-rossijskoj-federats/) · [Example published criteria](https://hamlog.online/club/nebo-cosmodromes/ad3c6f8ec0a71ddcd2a4390e9122fbf3/rules/)

Supported RCPW plans read the member callsigns from the club website. Each plan follows its own repeat rule. An award may count each member once or accept another contact on a different band or mode. The public HAMLOG results supply the credited contacts and the applicable threshold. AutoCQ compares the credited points with the published rates before looking for a new contact. [RCPW rules example](https://hamlog.online/club/rcpw/bbfc8b0d8f75b77c547a8cd342e70de3/rules/) · [RCPW member roster](https://rcpw.ru/members.html)

Each individual review identifies a public HAMLOG page and the SHA-256 of its normalized rule text. For an image-only rule document, the fingerprint can identify the exact PDF. A review includes a validated search plan or an explanation shown under the award list. If the document changes, AutoCQ evaluates it again. An independently saved edit takes precedence over the downloaded plan. [Reviewed award records](../src/HamlogReviewedAwards.json)

For a district-count award, HAMLOG supplies the credited codes and the current thresholds. An award with a single region searches the stations in that region. HAMLOG decides which district each contact adds to credit. [RDA district reference](https://rdaward.org/rda_rus.txt)

Point awards can also require distinct districts, a minimum number of contacts in a particular district, or contacts with named stations. AutoCQ compares secondary HAMLOG counters with their accepted QSO rows. For Kazan, HAMLOG checks 25 contacts and five distinct districts. For the TV Tower award, the standing includes the 60-point total and contacts with the required station group. [Kazan rules](https://hamlog.online/club/kazan/be5792cd277125309bc1216c86f36bba/rules/) · [TV Tower rules](https://hamlog.online/global/russia/r1a/a75954ec9180c819a1a6d4bb4e2fd522/rules/)

Event rules can use exact UTC intervals and award extra points on selected bands. AFARU events with dates stated in Moscow time use UTC+3 when compiling their start, finish and double-points day. For the supported Fifth Ocean events, the rule uses the published band list, named stations and the club's callsign roster. Expired cards retain their rules for checking historical credit. [AFARU roster](https://www.afaru.ru/index.php?doc=log&section=doc) · [Fifth Ocean roster](https://aviaham.org/callbook/all/)

District rules can specify an exact list of counties. The Saratov left-bank card requires eight districts from its published list on HF. Tula multiplies the sum of credited contact points by the number of distinct RDA districts. AutoCQ displays the HAMLOG standing for these awards and searches the relevant region. [Saratov Land rules](https://hamlog.online/global/russia/r4c/1025/rules/) · [Tula rules](https://hamlog.online/global/russia/r3p/485/rules/)

WHCG uses the table of accepted contacts to count Ecuadorian Maidenhead squares. A fresh four-character locator must add a new square and the same callsign, band and mode must still be eligible. FIRAC OE DIGI uses the public FIRAC award roster and the Austrian group published with the HAMLOG standings. A portable call can match its base callsign. AutoCQ counts each required station once. [WHCG rules](https://hamlog.online/club/hcdx/177/rules/) · [FIRAC OE rules](https://www.oevsv.at/oevsv/diplome/) · [Public FIRAC award data](https://www.firac.de/FIRAC-AWARD.zip)
