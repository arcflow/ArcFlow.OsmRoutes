# ArcFlow.OsmRoutes

**Daily-built Switzerland pedestrian routing graph (Itinero RouterDb), from OpenStreetMap.**

A tiny, single-purpose .NET 8 console app: download the latest
`switzerland-latest.osm.pbf` from [Geofabrik](https://download.geofabrik.de/) and build
`switzerland.pedestrian.routerdb` (an [Itinero](https://www.itinero.tech/) pedestrian graph).
A daily [GitHub Actions](.github/workflows/build-routerdb.yml) cron runs it and publishes the
result as a **GitHub Release**, so consumers can *download* an always-fresh routerdb instead of
building it themselves (the build is the slow, heavy part).

> Data: **© OpenStreetMap contributors**, published under **ODbL 1.0**. See
> [`LICENSE-DATA`](LICENSE-DATA) and [`NOTICE`](NOTICE). Code: **Apache-2.0** ([`LICENSE`](LICENSE)).

## Usage

```sh
# framework-dependent build (needs .NET 8 runtime)
./publish.cmd            # Windows  ->  bin/publish/ArcFlow.OsmRoutes.exe
# or run directly:
dotnet run -c Release -- --contract true
```

### Options

| Flag / env | Default | Meaning |
|---|---|---|
| `--pbf-url` / `OSMROUTES_PBF_URL` | Geofabrik switzerland-latest | Source `.osm.pbf` URL |
| `--pbf` / `OSMROUTES_PBF` | `switzerland-latest.osm.pbf` | Local pbf path |
| `--routerdb` / `OSMROUTES_ROUTERDB` | `switzerland.pedestrian.routerdb` | Output routerdb path |
| `--contract` / `OSMROUTES_CONTRACT` | `true` | Add a contracted graph (faster routing, slower/heavier build) |

The download does a **conditional GET**: it fetches Geofabrik's published `.md5` and skips the
download when the local pbf already matches. A `<routerdb>.md5` sidecar is written next to the
output.

## Consuming the published routerdb

Download the asset from the [`latest`](../../releases/latest) release and
`RouterDb.Deserialize` it. **Deserialize with the same Itinero major version** the release was
built with (stated in each release's notes).

## How it's built

`RouterDb` → `LoadOsmData(pbf, Vehicle.Pedestrian)` → optional
`AddContracted(Vehicle.Pedestrian.Shortest())` → `Serialize`. Itinero 1.5.1 is `netstandard2.0`
and runs on .NET 8.

## License

- **Code** — Apache-2.0 ([`LICENSE`](LICENSE)).
- **Produced data** (`*.routerdb` release assets) — ODbL 1.0 ([`LICENSE-DATA`](LICENSE-DATA)).
  Downstream users inherit ODbL **attribution + share-alike**.
