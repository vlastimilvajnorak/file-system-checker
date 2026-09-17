# FileSystem Change Tracker

ASP.NET Core REST API, které po ručním spuštění analyzuje zadaný adresář (včetně podstromu)
a hlásí změny oproti poslednímu spuštění: nové, změněné a odstraněné soubory (a nové/odstraněné
podadresáře). U každého souboru eviduje verzi.

---

## Zadání

*Cvičný úkol PUX design – Hledáme šikovného web developera!*

> Napište jednoduchý program, který bude umět detekovat změny v adresáři uvedeném na vstupu.
>
> Adresář se bude nacházet na filesystému, na kterém běží daný program (lokální filesystém). Při
> prvním spuštění si program obsah daného adresáře analyzuje a při každém dalším spuštění bude
> hlásit změny od svého posledního spuštění, tj:
>
> a) seznam nových souborů a podadresářů,
> b) seznam změněných souborů (změnou se rozumí změna obsahu daného souboru),
> c) seznam odstraněných souborů a podadresářů.
>
> U každého souboru evidujte číslo jeho aktuální verze (na začátku budou mít všechny soubory verzi 1,
> s každou detekovanou změnou daného souboru bude jeho verze navýšena o 1).
>
> Program realizujte jako jednoduchou ASP.NET aplikaci naprogramovanou v C#. UI vytvořte jako
> webovou aplikaci dle své volby (Core MVC, MVC, REST API)
>
> Můžete předpokládat, že velikost souborů v adresáři bude do 50 MB a že počet souborů v každém
> adresáři bude nanejvýš 100.
>
> Program se bude spouštět ručně z UI stiskem tlačítka nebo zavoláním REST API endpointu
> (nedetekujte změny filesystému automaticky). Pro perzistenci dat nepoužívejte databázi.
>
> V případě MVC bude UI obsahovat alespoň textbox (textový input) pro zadání cesty k analyzovanému
> adresáři, tlačítko pro spuštění analýzy a výpis jejího výsledku. V případě REST API bude cesta
> předána jako URL parametr.
>
> Své řešení stručně popište a zmiňte i jeho případná omezení. Pokud k vygenerování kódu použijete
> AI, uveďte to v popisu.

## Stručný popis řešení

- **ASP.NET Core Web API v C#** (.NET 10) + jednoduché statické **mini UI** (textbox na cestu,
  tlačítko, živý výpis průběhu a výsledku) nad stejnými REST endpointy.
- Analýza běží **na pozadí** a klient ji sleduje pollingem nebo přes SSE stream — endpointy a důvod
  tohoto návrhu jsou popsané níže v [TL;DR](#tldr--rychlý-start) a [Klíčových rozhodnutích](#klíčová-rozhodnutí).
- **Detekce změny obsahu** = streamovaný SHA-256 hash souboru; nové/změněné/odstraněné soubory
  a podadresáře se zjistí porovnáním s předchozím uloženým stavem (snapshotem).
- **Verzování** podle zadání: nový soubor verze 1, každá detekovaná změna verzi zvýší o 1.
- **Perzistence bez databáze** — jeden JSON snapshot na disku na analyzovaný adresář, mimo sledovaný
  strom, s atomickým zápisem.
- Podrobný popis komponent, návrhových rozhodnutí, omezení a možných rozšíření je v sekcích
  [Jak to funguje](#jak-to-funguje), [Klíčová rozhodnutí](#klíčová-rozhodnutí) a
  [Omezení a minusy](#omezení-a-minusy) níže.

## Technologický stack

- **.NET 10** / **C#** (`LangVersion` latest, `Nullable` a `ImplicitUsings` zapnuté)
- **ASP.NET Core Web API** (`Microsoft.NET.Sdk.Web`) — REST endpointy, `BackgroundService`
  (`AnalysisWorker`) pro zpracování analýz na pozadí, `System.Threading.Channels` jako fronta úloh
- **Server-Sent Events** (nativní `EventSource` v prohlížeči) pro živý stream průběhu analýzy
- **Swashbuckle / Microsoft.AspNetCore.OpenApi** — Swagger UI a OpenAPI dokument v Development
- **Mini UI**: statický HTML/CSS/JS (bez frontend frameworku) ve `wwwroot`
- **Perzistence**: JSON soubory na disku (bez databáze), atomický zápis
- **Testy**: xUnit, `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory` + in-memory
  TestServer) pro integrační testy HTTP vrstvy, `coverlet.collector` pro code coverage
- **Central Package Management** (`Directory.Packages.props`) a sdílené MSBuild vlastnosti
  (`Directory.Build.props`) napříč projekty solution

---

## TL;DR / rychlý start

Prerekvizita: **.NET 10 SDK**.

```bash
# z kořene repozitáře
dotnet run --project FileSystemChangeTracker.Api
# pak otevři http://localhost:5250/ (mini UI) nebo /swagger
```

Na `http://localhost:5250/` je **mini UI**: textbox pro cestu, tlačítko pro spuštění a živý
průběh + výpis výsledku (statická stránka nad REST API, průběh odebírá přes SSE).

- `POST /api/analyses?path=<cesta>` → `202` + `analysisId` (analýza běží na pozadí),
- `GET /api/analyses/{id}` → stav s průběžnými čítači (prošlé adresáře, zhashované soubory
  a bajty, právě zpracovávaný soubor) a **živě detekovanými změnami** (`result` se plní už
  během běhu, aktualizace ~1×/s); po dokončení finální výsledek: nové / změněné / odstraněné
  soubory a podadresáře, u každého souboru verze,
- `GET /api/analyses/{id}/events` → totéž jako **živý stream** (Server-Sent Events): událost
  `progress` každých 500 ms, závěrečná událost `result` se pošle **okamžitě** po dokončení
  či zrušení (ne až v dalším tiku) a stream ukončí,
- `GET /api/analyses/baseline?path=<cesta>` → existuje pro cestu výchozí stav? (kdy proběhla
  poslední analýza, kolik souborů sleduje, případně že je **neúplný** po nedokončeném prvním
  běhu, a zda pro cestu právě běží analýza) — mini UI podle toho rozlišuje první vs. opakovanou
  analýzu,
- `DELETE /api/analyses/baseline?path=<cesta>` → **reset výchozího stavu** (smaže snapshot,
  příští analýza založí baseline celého stromu znovu a nic nenahlásí; `204` úspěch, `404` bez
  baseline, `409` při běžící analýze či dočasně zamčeném souboru, `403` bez oprávnění) — mini UI
  ho nabízí pro každou existující baseline, typické použití je ale neúplná baseline
  po nedokončeném prvním běhu,
- `DELETE /api/analyses/{id}` → **zrušení běžící analýzy** (stav `Cancelled` + částečný výsledek
  s `partial: true`). U **prvního** běhu se zpracovaná část uloží jako neúplný výchozí stav
  (**soubor je sledovaný od prvního zhashování**); nad kompletní baseline se nic neukládá
  a příští dokončený běh nahlásí změny celého stromu znovu (nic se neztratí),
- `GET /api/browse?path=<cesta>` → procházení adresářů na serveru (prázdná cesta = na Windows
  seznam disků, jinde kořen `/`) — podklad pro ikonu procházení v UI (webová stránka nezná
  skutečné absolutní cesty).

První běh pro danou cestu je **baseline**: jen se založí snapshot, změny se nehlásí.

**Proč 202 + polling, a ne synchronní odpověď?** Synchronní endpoint by byl o ~200 řádků kratší,
ale doba hashování není shora omezená (limit „≤100 souborů" je *na adresář*, rekurze ho ruší),
takže by narážel na HTTP timeouty a držel request sloty. Vědomé rozhodnutí, plné zdůvodnění
v sekci [Klíčová rozhodnutí](#klíčová-rozhodnutí).

---

## Jak to funguje

Analýza je **dlouhotrvající operace nad neznámě velkým stromem**, proto je oddělená od HTTP requestu
(pattern *async request-reply*):

```
POST /api/analyses?path=...   → založí úlohu, vrátí 202 + analysisId  (běží na pozadí)
GET  /api/analyses/{id}        → polling stavu; po dokončení vrátí výsledek
```

Tok jedné analýzy (`AnalysisService`): načti poslední snapshot → projdi strom a spočítej hashe →
porovnej s předchozím snapshotem → ulož nový snapshot → vrať změny.

### Komponenty

| Komponenta | Odpovědnost |
|---|---|
| `AnalysesController` | REST endpointy analýz a baseline, validace vstupu |
| `BrowseController` | procházení adresářů na serveru (podklad pro výběr cesty v UI) |
| `AnalysisRegistry` | in-memory registr úloh; idempotence per cesta |
| `AnalysisBacklog` | fronta úloh (`System.Threading.Channels`) |
| `AnalysisWorker` | `BackgroundService`; zpracování s omezenou souběžností (`Parallel.ForEachAsync`) |
| `AnalysisService` | orchestrace jedné analýzy |
| `DirectoryScanner` | průchod stromem adresář po adresáři (BFS), skip reparse pointů; selhání podadresáře neshodí analýzu |
| `Sha256FileHasher` | streamovaný SHA-256 (obsah se nenačítá celý do paměti) |
| `SnapshotComparer` | čistá porovnávací logika a verzování (jednotkově testovaná) |
| `JsonSnapshotStore` | perzistence snapshotu do JSON (bez DB), atomický zápis |
| `PathPolicy` | normalizace cest, klíč snapshotu, chování dle platformy |

---

## Klíčová rozhodnutí

- **Async + polling, ne synchronní odpověď.** Je to webová služba s více souběžnými requesty a doba
  hashování není shora omezená (limit „≤100 souborů" je *na adresář*, rekurze ho ruší). Držet HTTP
  request otevřený po neohraničenou dobu by naráželo na timeouty a vyčerpávalo request sloty.
- **Detekce změny = hash obsahu (SHA-256).** Zadání definuje změnu jako změnu obsahu; hash je
  nejpřesnější. Počítá se streamově.
- **Perzistence bez DB = JSON snapshot na disku**, jeden soubor na analyzovaný adresář, **mimo**
  sledovaný strom (Windows `%LOCALAPPDATA%\FileSystemChangeTracker\snapshots\{hash-cesty}.json`,
  Linux `~/.local/share/FileSystemChangeTracker/snapshots/…`). Umístění
  záměrně nezávisí na ContentRoot/cwd — ten se liší podle způsobu spuštění (VS/F5, `dotnet run`,
  přímé exe) a snapshoty by se rozpadly do více úložišť, takže by opakovaná analýza vypadala jako
  první. Když analyzovaný strom úložiště obsahuje (např. analýza celého `C:\`), skener adresář
  úložiště přeskočí — analýza nesleduje vlastní stavové soubory.
  Zápis je atomický (zápis do temp souboru → přejmenování).
  **Kdy se snapshot zapisuje:** po dokončeném běhu vždy. Rozpracovaný stav (merge po zrušení,
  průběžný checkpoint) **jen tehdy, když rozšiřuje neúplnou nebo chybějící baseline** — tedy
  u prvního běhu, který nedoběhl: zpracovaná část se uloží (soubor je sledovaný od prvního
  zhashování), nestihnutý zbytek se převezme z minulého stavu (stejný mechanismus jako
  u nečitelných podstromů), nikdy nevznikne falešné „smazáno". Taková baseline se ve
  snapshotu označí jako **neúplná** (vč. důvodu: zrušená uživatelem vs. přerušená pádem
  procesu); UI na to upozorní a nabídne volbu **pokračovat** (nezahrnuté soubory se nahlásí
  jako nové, verze 1), nebo **resetovat výchozí stav** (`DELETE /api/analyses/baseline`).
  Příznak zmizí prvním dokončeným během. Reset je v UI dostupný pro každou existující
  baseline — i kompletní stav tak jde založit od nuly.
  **Nad kompletní baseline se rozpracovaný stav nezapisuje nikdy.** Příští běh stejně
  hashuje celý strom znovu (zápis by neušetřil práci) a hlavně by „spotřeboval" změny,
  které se nikomu nenahlásily — po pádu či zastavení aplikace uprostřed skenu, nebo po
  selhání finálního zápisu by je příští běh už považoval za známé. Zrušený běh nad kompletní
  baseline proto vrátí částečný výsledek a baseline nechá beze změny; příští dokončený běh
  změny nahlásí znovu (včetně těch už ukázaných). Zadání „změny od posledního spuštění"
  tak platí vždy vůči poslednímu *dokončenému* nebo *baseline zakládajícímu* běhu.
  Neošetřená chyba snapshot nepřepíše.
- **Poškozený snapshot** (nevalidní JSON) analýzu neshodí: běh založí výchozí stav znovu
  (verze od 1), ale zapíše to do logu a nahlásí **varováním ve výsledku** — nikdy potichu.
- **Verzování:** nový soubor = verze 1; změněný = předchozí verze + 1; nezměněný = verze zůstává;
  odstraněný se hlásí s poslední známou verzí. Smazání a opětovné vytvoření na stejné cestě = nový
  soubor s verzí 1 — pokud smazání zachytila některá analýza; smazání i znovuvytvoření **mezi**
  dvěma běhy je od modifikace nerozlišitelné (snapshot nese jen cestu+hash+verzi) a nahlásí se
  jako změněný. Přejmenování = odstranění + nový soubor (bez sledování file-id).
- **Adresáře** se hlásí jako nové/odstraněné, ale **nemají verzi** a kategorie „změněný adresář"
  neexistuje.
- **Idempotence:** dokud pro danou cestu analýza běží, opakovaný POST vrátí stejné `analysisId`
  (nehrozí vícenásobné spuštění téže práce). Po dokončení založí další POST novou analýzu.
- **Multiplatformnost:** cesty přes `System.IO.Path`, normalizace `Path.GetFullPath`, relativní cesty
  se separátorem `/`, časy v UTC. Porovnávání cest respektuje platformu (Windows case-insensitive,
  Linux case-sensitive); klíč snapshotu je na Windows case-fold, aby `C:\Data` a `c:\data` daly stejný
  soubor.

---

## Omezení a minusy

- **Stav úloh je jen v paměti procesu.** Restart aplikace (i recyklace IIS app poolu) ztratí informace
  o běžících i dokončených analýzách. Snapshot na disku ale zůstává, takže další analýza navazuje od
  posledního uloženého stavu — ztratí se jen „výsledek poslední analýzy", ne historie verzí. Záznamy
  dokončených analýz se z paměti neuvolňují (žádná evikce) — při ručním spouštění je jich řádově málo,
  dlouhodobě běžící instance s velmi častým spouštěním by evikci potřebovala.
- **Návrh počítá s jednou instancí** nad lokálním filesystémem. In-memory registr a lokální úložiště
  snapshotů nefungují přes více instancí/replik (viz níže).
- **Průběh bez procent.** Polling i SSE stream vrací průběžné čítače (adresáře, soubory, bajty,
  aktuální soubor) a živě detekované změny (tentýž comparer jako finální výsledek, počítáno nad
  mezistavem skenu ~1×/s — díky carry-over sémantice bez falešných „smazáno"), ale ne procenta —
  celkový počet souborů není bez druhého průchodu stromem předem znám. SSE bylo zvoleno místo
  WebSocketu záměrně: průběh je jednosměrný tok a SSE je obyčejné HTTP bez další infrastruktury
  (v prohlížeči nativní `EventSource`).
- **Vždy „strict" hashování.** Hash se počítá pro každý soubor při každé analýze. U velkých stromů to
  může být pomalé; není rychlý režim (size+mtime). Snapshot proto záměrně ukládá jen cestu, hash
  a verzi — rychlý režim by vyžadoval doplnit size/mtime do snapshotu (viz tabulka rozšíření níže).
- **Bez stránkování výstupu.** Při ohromném množství změn může být odpověď velká.
- **Živé porovnání je O(počet souborů) za jeden mezistav.** Nad stromem se statisíci soubory
  trvá jedno porovnání stovky ms; interval mezistavů se proto adaptivně prodlužuje tak, aby
  porovnávání nespotřebovalo víc než ~10 % času skeneru. Dotaz „leží cesta v neprojitém
  podstromu?" jde po rodičích cesty (O(hloubka)), ne přes seznam neprojitých adresářů —
  při zrušeném BFS nad celým diskem jich jsou desítky tisíc a lineární varianta by skener
  zablokovala tak, že by nešel ani zrušit.
- **Přejmenování** se hlásí jako odstranění + nový soubor (verze se začíná od 1).
- **Neúplná baseline a checkpointy.** Dokud první běh nedoběhl, ukládá se rozpracovaný stav
  (merge po zrušení, checkpoint). Změna souboru, který už neúplná baseline sledovala, se
  v takovém zápisu pohltí, a pokud ten běh skončí pádem procesu, nikdo ji nenahlásí. Týká se
  jen stavu, na který UI výslovně upozorňuje a nabízí reset; nad kompletní baseline se
  rozpracovaný stav nezapisuje a report se ztratit nemůže.
- **Symlinky / reparse pointy se přeskakují** (kvůli cyklům) — obsah za nimi se nesleduje.
- **Skener nefiltruje žádné adresáře.** Zadání chce hlásit všechny změny, takže se prochází celý
  podstrom (včetně `.git`, `bin`, `obj` apod.). Nad vývojovým stromem proto může být výstup
  „ukecaný"; kdyby to vadilo, šlo by snadno doplnit konfigurovatelný seznam vylučovaných názvů
  adresářů — záměrně to ale není v základním řešení, aby chování neodbočovalo od zadání.
- **Žádná autentizace/autorizace.** Endpointy (vč. `GET /api/browse`, které vypisuje adresářovou
  strukturu) čtou libovolnou cestu, kam má proces přístup. Pokud by
  byl vystaven nedůvěryhodným klientům, je to riziko (čtení filesystému serveru).
- **Temp soubor** může zůstat ležet jen po *tvrdém pádu procesu* uprostřed zápisu — běžná selhání
  a zrušení zápisu ho uklidí. Poslední platný snapshot jím není ohrožen v žádném případě.

---

## Kdy by se musela změnit implementace

| Změna prostředí / požadavku | Co by bylo potřeba |
|---|---|
| **Velké / hluboké stromy, dlouhý sken** | rychlý režim (size+mtime, hash jen u změněných), inkrementální nebo řádkový snapshot, stránkování výstupu; průběžné čítače i zrušení (`DELETE /api/analyses/{id}`) už jsou hotové |
| **Více instancí** (IIS web garden, K8s `replicas > 1`, load balancer) | sdílené úložiště snapshotů (sdílený volume / object storage) místo lokálního adresáře; **distribuovaná** koordinace (distributed lock) místo in-memory zámku; sdílený stav úloh nebo sticky sessions, protože POST a následný GET mohou trefit jinou repliku. (DB je zadáním zakázána, takže by šlo o jiné sdílené úložiště.) |
| **Stav úloh má přežít restart** | perzistovat registr úloh (bez DB např. do souboru), nebo vědomě akceptovat ztrátu (současný stav) |
| **Živé UI s průběhem** | hotovo přes SSE (`/events`); SignalR/WebSocket by měl smysl až pro obousměrnou komunikaci |
| **Spolehlivá detekce přejmenování** | sledování file-id / inode (platformově specifické) |
| **Sledování obsahu za symlinky** | řízené následování s detekcí cyklů (evidence navštívených cílů) |
| **Veřejné vystavení API** | autentizace + allowlist povolených kořenových cest, aby nešlo číst libovolný FS |
| **Soubory výrazně nad 50 MB / mnoho souborů** | čtení už je streamové; navíc zvážit paralelní hashování v rámci jedné analýzy (dnes se paralelizují jen analýzy mezi sebou) |

---

## Nasazení v cloudu

Aplikace je navržena pro **jednu instanci nad lokálním filesystémem** (tak to říká i zadání).

**Jedna instance — funguje:** kontejner s aplikací, kde je analyzovaný adresář i adresář snapshotů
(`SnapshotsDirectory` — v kontejneru nastavit explicitně) připojený jako **persistent volume**;
v Kubernetes `replicas: 1`. Stav úloh v paměti stačí, protože všechny requesty obsluhuje jeden proces.

**Více replik — základní řešení nestačí:**
- každý pod má vlastní paměť → in-memory registr úloh se nesdílí (dva requesty pro stejnou cestu na
  různých podech založí dvě analýzy),
- každý pod má typicky vlastní filesystém → stejná cesta nemusí být stejná data (bez sdíleného volume),
- snapshot v lokálním úložišti jedné repliky jiná replika neuvidí,
- `POST` může obsloužit pod A, ale následný `GET` pod B, který dané `analysisId` nezná.

**Co by multi-replica vyžadovalo:** sdílené úložiště snapshotů (sdílený volume / objektové úložiště),
distribuovanou koordinaci (distributed lock) místo in-memory zámku a sdílený stav úloh nebo sticky
sessions. Protože zadání zakazuje databázi, šlo by o jiné sdílené úložiště než DB. To už je ale mimo
rozsah tohoto úkolu.

---

## Build, spuštění, testy

```bash
# z kořene repozitáře
dotnet build FileSystemChangeTracker.slnx
dotnet test  FileSystemChangeTracker.slnx
dotnet run   --project FileSystemChangeTracker.Api
```

Testy: jednotkové (comparer, skener nad dočasným adresářem, úložiště, registr, hasher,
vazba konfigurace) a **integrační testy HTTP vrstvy** (`AnalysesApiTests`,
`WebApplicationFactory` + in-memory TestServer, analýza nahrazená řiditelným stubem):
validace cesty, idempotence POST, životní cyklus úlohy vč. zrušení a selhání, SSE stream,
baseline a procházeč.

Ruční vyzkoušení endpointů: `FileSystemChangeTracker.Api/FileSystemChangeTracker.Api.http`.
V Development je k dispozici **Swagger UI na `/swagger`** (interaktivní dokumentace) nad OpenAPI
dokumentem na `/openapi/v1.json`.

### Oprávnění (hosting Kestrel)

Pod Kestrelem běží proces pod identitou účtu, který ho spustil, a čte tam, kam tento účet má přístup:

- **Vývoj:** přihlášený uživatel — běžně čte své cesty bez dalšího nastavení.
- **Windows Service / systemd:** uděl servisnímu účtu read na sledovaný adresář.
- **Síťový share (UNC):** účet musí mít síťovou identitu (doménový účet / gMSA), ne lokální virtuální účet.

Chování při nedostatku práv: **nedostupný kořen** vrátí `403`, **jednotlivé** nečitelné položky uvnitř
stromu se zaznamenají jako `warning` (analýza nespadne). Soubory otevřené jiným procesem pro
zápis (aktivní log, otevřený dokument) se **čtou** (hasher otevírá s `FileShare.ReadWrite`; hash je
momentka, příští běh porovná znovu). Soubor, který existuje, ale opravdu nejde přečíst
(výhradní zámek, chybějící oprávnění), se **nehlásí jako smazaný** — jeho poslední známý
záznam včetně verze se ponechá a porovná se znovu při dalším běhu. Nový soubor, který je hned od
začátku nečitelný, se objeví jako „nový" až při prvním čitelném běhu. Totéž platí pro celé
adresáře: **neprojditelný podadresář** (odebraná práva, zamčená položka během enumerace) analýzu
neshodí — zaznamená se warning a poslední známý stav celého jeho podstromu se ponechá beze změny.

### Konfigurace (`appsettings.json`, sekce `Analysis`)

Načítají se přes `IOptions<AnalysisOptions>`; výchozí hodnoty jsou v `appsettings.json`,
kromě `SnapshotsDirectory`, jehož default je záměrně v kódu (jedině
`Environment.GetFolderPath` dá použitelné umístění na všech platformách — `%LOCALAPPDATA%`
by na Linuxu zůstalo doslovným názvem adresáře).

| Klíč | Význam | Výchozí |
|---|---|---|
| `SnapshotsDirectory` | umístění snapshotů; proměnné prostředí se expandují, relativní hodnota se vztahuje k ContentRoot, cesta se plně normalizuje | Windows `%LOCALAPPDATA%\FileSystemChangeTracker\snapshots`, Linux `~/.local/share/FileSystemChangeTracker/snapshots` (nezávislé na způsobu spuštění — VS/F5, `dotnet run` i přímé exe sdílí totéž úložiště) |
| `MaxConcurrentAnalyses` | max. souběžně běžících analýz na pozadí | `8` |
| `CheckpointInterval` | jak často se během **prvního (nedokončeného)** běhu ukládá průběžný mezistav (pád procesu ztratí nejvýše takto starou práci); nad kompletní baseline se neukládá; `00:00:00` = vypnuto | `00:00:30` |

---

## Licence

[GNU GPL v3](LICENSE.txt).
