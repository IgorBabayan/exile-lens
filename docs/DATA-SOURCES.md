# Data sources, rules and rate limits

## Official trade (pathofexile.com)

| Purpose | PoE 1 | PoE 2 |
|---|---|---|
| Search | `POST /api/trade/search/{league}` | `POST /api/trade2/search/poe2/{league}` |
| Fetch listings (≤10 ids) | `GET /api/trade/fetch/{ids}?query={id}` | `GET /api/trade2/fetch/{ids}?query={id}` |
| Leagues | `GET /api/trade/data/leagues` (realm `pc`) | `GET /api/trade2/data/leagues` (realm `poe2`) |
| Stat ids | `GET /api/trade/data/stats` | `GET /api/trade2/data/stats` |
| Website | `/trade/search/{league}/{id}` | `/trade2/search/poe2/{league}/{id}` |

- These endpoints back the trade website and are **not** part of GGG's documented OAuth API. Exile Lens uses them
  read-only: one search per user action, no login, no cookies, and nothing is purchased or whispered automatically.
- Rate limits are read from `X-Rate-Limit-Policy`, `X-Rate-Limit-Rules`, `X-Rate-Limit-{rule}` and
  `X-Rate-Limit-{rule}-State`, with one request of headroom per window. `429` responses honour `Retry-After`.
  Limits are never bypassed (no proxies, no account rotation).
- Requests send a descriptive `User-Agent` with a contact URL, as GGG's developer docs ask.
- Stat definitions are cached in `<data>/cache/trade-stats-{game}.json` for 24 h and used offline.

## poe.ninja

- `GET /{poe1|poe2}/api/economy/exchange/current/overview?league=&type=` (documented at https://poe.ninja/docs/api).
- Used for currency rates (disenchant valuation, listing conversion) and bulk items: currency, fragments, essences,
  runes, omens, scarabs, divination cards.
- Responses are cached in memory for 10 minutes and revalidated with ETags. Data is fetched only when a price check
  needs it.
- poe.ninja recommends that widely distributed desktop clients go through their own caching backend.
  *Settings → Trade → poe.ninja proxy* accepts a base URL for such a proxy; leaving it empty calls poe.ninja directly.

## Reward rules

The rules are verified against the sources listed inside each JSON file and are shown in the app on the
Disenchantment page.

**PoE 2, disenchanting (vendor).** Magic → 1 Transmutation Shard; Rare → 1 Regal Shard, or 2 with six
modifiers (0.1.0c); Unique → 1 Chance Shard. Normal items cannot be disenchanted.

**PoE 2, salvage bench (separate operation).** 1 Artificer's Shard per rune socket. One quality currency per 5%
quality, with a chance at one more between breakpoints (shown as min–max and expected value). The quality currency
is Armourer's Scrap for armour, Blacksmith's Whetstone for martial weapons, Arcanist's Etcher for wands, staves and
sceptres, and Glassblower's Bauble for flasks. Jewellery, jewels and gems cannot be salvaged.

**PoE 1, single-item vendor recipes.** 6-link → 20 Orbs of Fusing (outranks 6 sockets); 6 sockets →
7 Jeweller's Orbs; linked R-G-B → 1 Chromatic Orb; gem at 20% quality → 1 Gemcutter's Prism.

**Shard valuation.** A shard uses its own exchange price when poe.ninja lists one. Otherwise it is valued at the
full currency's price ÷ stack size (10). All money math uses `decimal`.

**Updating rules without a release.** Copy the JSON from `src/ExileLens.Game.Poe2/Data/` to
`<data>/rules/poe2/rewards.json` (or `poe1`), edit it, and restart the app.

## Map modifier catalog

`Data/map-modifiers.json` in each game module seeds known modifiers with stable ids and categories. A modifier
seen on a real map that isn't in the catalog is recorded in `discovered-modifiers.json` under a deterministic id,
appears on the Map Modifiers page, and stays **Unclassified** until you classify it. An unknown modifier never
counts as safe.
