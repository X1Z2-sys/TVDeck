# TVDeck — Proposals & research log

Every package name below was read from a real APK manifest or the source repo; every setting/hostname from official docs.

## Shipped (2026-10-03)

| Item | What it does | Undo | Source |
|---|---|---|---|
| Settings snapshots | Before the first change per TV per session (tweak, debloat, recipe) TVDeck saves every setting it can change + the disabled-app list to Documents\TVDeck\Snapshots. Backup → Settings snapshots restores it, showing each change first and saving the current state before restoring. | The restore itself is snapshotted | — |
| Recommended tweaks | `recommend` flag on 9 tweaks (low risk, checkable, reversible, nothing lost). Tweaks page: "Apply N recommended" with a confirm list. Never overrides a value the user picked (e.g. animations Off stays Off). | Each card states its undo | — |
| Presets | Built-in recipes now reference tweaks by id (`{"tweak": id, "value": v}`), skipped automatically on TVs they don't fit: Recommended for every TV · Privacy & fewer ads · Speed up a slow box · Home media center · Clean home screen · Game streaming | Tweaks page / Apps → Make home screen | — |
| Private DNS dropdown | One choice: Default · AdGuard · AdGuard Family · Control D (p2 = ads & trackers) · Quad9 (malware) | "Default (your router)" | adguard-dns.io, docs.controld.com, quad9.net |
| Optimize apps now | `cmd package bg-dexopt-job` — Android's own overnight optimization, now | none needed | AOSP |
| Clear all app caches | `pm trim-caches 999999999999` | none needed | AOSP |
| Skip Google's internet check | `captive_portal_mode 0` | delete key | AOSP |
| Button Mapper / tvQuickActions / Projectivy grants | `pm grant … WRITE_SECURE_SETTINGS` (+ appops for tvQuickActions); shown only when the app is installed | revoke | setup.buttonmapper.app, tvdevinfo.com, r/tcltvs |
| Debloat: Sony Bravia | supermarsx `i13` preset, 40 pkgs (disable-only), brand-filtered | Restore | supermarsx/sony-bravia-adb-scripts |
| Debloat: TCL | livvaa categories 1 (recommended) + 3 (optional); original uninstalls, TVDeck only disables | Restore | livvaa/TCL-Google-TV-Debloat-Optimizer |
| Debloat: Xiaomi | spocky Projectivy installer list, 38 pkgs | Restore | spocky/miproja1 franky.json |
| Catalog | ChillHub `app.lumoslabs.chillhub`, AT4K `com.overdevs.at4k`, Webview Kiosk `uk.nktnet.webviewkiosk`, UFM Pro `za.kilowatch.ultimatefilemanager`, + Couchy/Monet/BareLauncher/ADB Auto-Enable/AndroidTVBluetooth | uninstall | APK manifests |
| Store links | Google TV uses `https://play.google.com/store/apps/details?id=` (Play 52.x broke `market://launch`) | — | r/GoogleTV 1vsvu6h |
| Brand logos | 127 logos + 269 aliases: every company on Wikipedia's *List of television manufacturers* + Android/Google TV OEMs, official Wikidata logo (P154) rendered by Commons. All public domain / CC / Apache, credited in Assets/Brands/CREDITS.txt | — | Wikidata, Wikimedia Commons |

## Rejected after verification

| Proposal | Why not |
|---|---|
| `setprop net.tcp.buffersize.*`, `persist.sys.ui.hw`, `debug.hwui.renderer` "speed" tweaks (scout) | Shell can't set `persist.*`; modern Android ignores tcp buffersize props; `debug.*` resets on reboot. Placebo. |
| Mullvad encrypted DNS | Mullvad discontinues its public DoH/DoT service on 2026-11-02. |
| NextDNS preset | Blocking needs a per-account config ID; the bare host does nothing. |
| Glance AI screensaver ads / Katniss RAM saver (scout) | Already in the Google TV debloater (`com.google.android.backdrop`, `com.google.android.katniss`). |
| `pm grant … SYSTEM_ALERT_WINDOW / PACKAGE_USAGE_STATS` | Those are app-ops; done with `appops set` instead. |
| Previous draft's `com.samba.tv`, `com.xiaomi.mimiradiotracker`, `com.flirc.tvquickactions`, `com.at4k.launcher` | Don't exist. Real: `tv.samba.ssm`, —, `dev.vodik7.tvquickactions`, `com.overdevs.at4k`. |

## Open proposals (need your approval)

1. **Stay connected on Android 14+** — guided install of ADB Auto-Enable (`com.tpn.adbautoenable`, now in the catalog) + one-time pairing at `http://<tv>:9093`; TVDeck then reconnects on :5555. Opens ADB on the LAN → opt-in with a warning. Effort M.
2. **Remote button remapping helper** — the grants are shipped; next step is a guided flow on the Button Mapper / tvQuickActions store cards (install → grant → open). Effort S.
3. **Bluetooth quick-connect** — `com.saihgupr.btcontrol` broadcast intents from the Screen & remote page. Needs its README's exact intent verified. Effort S–M.
4. **"Add tweak" step in the recipe editor** — built-in presets use tweak steps; user recipes can only add shell steps today. Effort S.
