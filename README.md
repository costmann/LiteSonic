# LiteSonic

Server di streaming musicale compatibile **Subsonic / OpenSubsonic** (come Navidrome), scritto in .NET 10 e pensato per macchine con poca RAM (Raspberry Pi, vecchi PC, mini-PC donati).

Funziona con i client Subsonic esistenti: Symfonium, Substreamer, DSub, Ultrasonic, Tempo (Android), play:Sub, Amperfy (iOS), Feishin, Sonixd, Supersonic (desktop).

## Consumo di memoria

Misurato con ~1200 richieste API e streaming consecutivi:

| Build | RSS |
|---|---|
| Native AOT (`-p:PublishAot=true`) | **~30 MB** |
| JIT (Debug, `dotnet run`) | ~72 MB |

Le scelte che lo tengono leggero:

- **Native AOT**: niente JIT e niente metadati caricati a runtime, avvio istantaneo.
- **GC workstation non concorrente** con `ConserveMemory=5`: un solo heap compattato in modo aggressivo.
- **`InvariantGlobalization`**: niente ICU (~30 MB in meno).
- **SQLite senza ORM**: SQL scritto a mano, cache di pagina limitata a 1 MB, WAL e niente mmap.
- **Scansione in streaming**: un file alla volta, transazioni da 500. La libreria non viene mai caricata tutta in RAM, e alle scansioni successive si rileggono solo i file con mtime o dimensione cambiati.
- **Streaming con `sendfile`** e richieste Range: i byte vanno dal disco al socket senza passare per un buffer gestito.
- **Copertine** incorporate estratte una sola volta e messe in cache **su disco**, non in RAM.
- **Transcodifica** con `ffmpeg` esterno a un solo thread, con un tetto di processi concorrenti (`MaxConcurrentTranscodes`, predefinito 1).
- Risposte XML/JSON serializzate a mano: niente reflection, compatibile con trimming e AOT.

## Avvio rapido

```bash
dotnet run -- --LiteSonic:MusicFolder=/percorso/musica
```

Al primo avvio viene creato l'utente `admin`. Se `AdminPassword` è vuota, la password generata compare nel log. Poi collega un client a `http://<ip>:4533`.

## Pubblicazione per un dispositivo debole

Native AOT va compilato **sulla stessa piattaforma di destinazione** (sistema operativo e architettura), quindi su un Raspberry Pi o con Docker:

```bash
dotnet publish -c Release -r linux-arm64 -p:PublishAot=true -o out
```

Se non puoi compilare con AOT, la seconda scelta più leggera è un'app self-contained con trimming:

```bash
dotnet publish -c Release -r linux-arm64 --self-contained -p:PublishTrimmed=true -o out
```

Copia `out/` sul dispositivo e avvia `./litesonic`.

Docker (immagine "chiseled" senza shell; non contiene ffmpeg, quindi la transcodifica è disattivata):

```bash
docker build -t litesonic .
docker run -d -p 4533:4533 -v /musica:/music:ro -v litesonic-data:/data litesonic
```

### Limitare la RAM

Per impostare un tetto rigido all'heap gestito:

```bash
DOTNET_GCHeapHardLimit=0x3200000 ./litesonic   # 50 MB
```

## Configurazione

Si configura con `appsettings.json`, con variabili d'ambiente (`LiteSonic__MusicFolder=...`) oppure da riga di comando (`--LiteSonic:Port=4533`).

| Chiave | Default | Note |
|---|---|---|
| `MusicFolder` | `./music` | |
| `DataFolder` | `./data` | Database, chiave di cifratura, cache delle copertine |
| `Port` | `4533` | |
| `AdminUser` / `AdminPassword` | `admin` / generata | Usati solo al primo avvio |
| `ScanIntervalMinutes` | `60` | `0` = scansione solo all'avvio e con `startScan` |
| `WatchMusicFolder` | `true` | Scansione incrementale automatica quando cambiano i file (inotify) |
| `WatchDelaySeconds` | `10` | Attesa dopo l'ultima modifica prima di scansionare |
| `FfmpegPath` | `ffmpeg` | Vuoto o non trovato = transcodifica disattivata |
| `MaxConcurrentTranscodes` | `1` | Oltre questo limite il server risponde 503 dopo 10 s |
| `DefaultTranscodeBitRate` | `128` | kbps |

## API implementate

`ping`, `getLicense`, `getMusicFolders`, `getIndexes`, `getArtists`, `getArtist`, `getAlbum`, `getSong`, `getMusicDirectory`, `getAlbumList`/`2` (tutti i tipi), `getRandomSongs`, `getGenres`, `getSongsByGenre`, `search2`/`3` (la query vuota restituisce tutto, serve alla sincronizzazione di Symfonium), `stream` (Range, `maxBitRate`, `format`, `timeOffset`), `download`, `getCoverArt`, `star`/`unstar`, `getStarred`/`2`, `scrobble`, playlist (`get`/`create`/`update`/`delete`), utenti (`getUser`/`s`, `createUser`, `deleteUser`, `changePassword`), `startScan`, `getScanStatus`.

Autenticazione: token `t`+`s` (md5), password `p` in chiaro o `enc:`, sia GET sia form POST, risposte XML o JSON (`f=json`).

## Limiti noti / prossimi passi

- Nessuna interfaccia web: si usano i client Subsonic.
- `getCoverArt` ignora `size` e restituisce l'immagine originale. Ridimensionare in memoria costerebbe RAM; un'alternativa è generare miniature con ffmpeg e tenerle in cache su disco.
- Il file watcher usa inotify (un watch per cartella): con librerie molto grandi può servire alzare `fs.inotify.max_user_watches`. Se non è disponibile resta la scansione periodica. I client che tengono una cache locale (es. substreamer) possono richiedere un aggiornamento manuale della libreria.
- La ricerca usa `LIKE`. Per librerie enormi si può passare a SQLite FTS5.
- Per stare dietro un reverse proxy con HTTPS usa Caddy o nginx: Kestrel qui serve solo HTTP.
