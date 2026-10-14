# contributions/

Signatures named by users in the app's **unknown signature** pop-up, sent with *Send to the GitHub
repository* checked (opt-in, control panel → *Send named signatures to GitHub*).

One folder per contribution: `YYYYMMDD_HHMMSS_rs<RS>_<name>/`

| File | Content |
|---|---|
| `signature.json` | `name`, `kind`, `observedRS`, `count`, `baseRS` (= observedRS / count), `notes`, `gameVersion`, `learnedAt`, `appVersion` |
| `plate.png` | crop of the in-game RS plate (pin icon + number) only, never the full screen |

To fold confirmed entries into the shipped database, add them to `data/<version>/materials.json`
(check `baseRS` against other contributions first: one sighting can be a mixed cluster).