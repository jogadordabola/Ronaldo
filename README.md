# Ronaldo

A League of Legends companion app for Windows. It watches the League client and, the moment
you hover or lock a champion, shows the rune pages and item builds people are actually
winning with — then applies them with one click.

![Patch](https://img.shields.io/badge/patch-16.16-9F7AEA)
![.NET](https://img.shields.io/badge/.NET-8.0--windows-512BD4)

## What it does

**Champion select**
- Three distinct rune pages per champion, ranked by pick rate, each with win rate and sample size
- Item build per page, filtered to the games that ran *that keystone*
- Starting items, boots, summoner spells and situational items, all as icons
- Click any page to apply it to the client; the top page can auto-apply
- Rank and region filters (Platinum+ through Challenger, World or a single server)
- Manual role override — needed in Practice Tool and blind pick, where the client reports no
  assigned position and the role would otherwise be guessed from play rate

**In game**
- Loading-screen style scoreboard: both teams, five and five, with champion, summoner spells,
  ranked stats and champion mastery
- Win rate for every player, counted from their last 20 games. Riot only publishes loss counts
  for your own account, so for everyone else the record is tallied from their match history
  rather than read off their profile
- Click any player to open their profile

**Profile**
- Rank, LP and W/L for solo and flex, with tier crests
- Most-played champions across recent games, with win rate
- Match history, and the full end-of-game scoreboard for any match
- Click any player on a match scoreboard to open *their* profile, and keep drilling
- LP gained or lost per ranked game

**Automation** (all optional, in the settings menu)
- Auto-accept queue
- Auto-apply the top rune page
- Import the build as an in-game item set, removed again once the game ends

## Where the data comes from

| Source | Used for |
|---|---|
| [op.gg](https://op.gg) | Rune pages with pick/win rates, item builds, summoner spells, positions |
| [Lolalytics](https://lolalytics.com) | Item builds filtered to a specific keystone |
| League Client (LCU) | Your profile, rank, match history, live game, mastery, item sets, and other players' recent form |
| [Community Dragon](https://communitydragon.org) | Champion, item, rune and rank icons |

No Riot API key and no account linking is required. The app talks to the League client over
the local lockfile session it is already authenticated with, and reads the two stats sites
over plain HTTPS. That is also the reason for some of the limits below: everything about other
players comes from what the client itself is willing to serve, not from a server-side key.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.

```
dotnet build ronaldo.sln
dotnet run --project ronaldo/ronaldo.csproj
```

Settings live in `%LOCALAPPDATA%\ronaldo\`, alongside a small icon cache.

## Known limitations

- **LP per game** is derived from snapshots taken around your games, because the client keeps
  no historical LP. It therefore only appears for games played while the app was running.
- **Other players' losses are withheld by Riot.** The client returns their ranked wins but
  reports `losses: 0` for anyone but you, which is why win rates for other players are counted
  from their match history instead. Treating that zero as real would put every opponent on a
  flawless record.
- **Win rates for other players cover their last 20 games**, not the split. The client serves
  at most twenty matches per player and ignores paging, so the window cannot be widened.
  Customs, Practice Tool, bot games and tutorials are excluded from the tally.
- **Other players' match history** can be restricted by Riot, and the surviving route differs
  by client build. Where it comes back empty the app falls back to showing their win count
  alone, and logs the endpoints it tried to `%LOCALAPPDATA%\ronaldo\match-history-probe.txt`.
- The client only serves **recent** matches, so "most played" is a form guide over that
  window rather than a career total.
- Rune pages are written under the name `Ronaldo Build`, and item sets under `Ronaldo · …`.
  Only pages and sets with those names are ever deleted; your own are left alone.

## Disclaimer

Ronaldo is an unofficial project, not endorsed by or affiliated with Riot Games. League of
Legends and Riot Games are trademarks of Riot Games, Inc.
