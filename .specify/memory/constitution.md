# Project Constitution — Pipelines Explorer

Principi non negoziabili per entrambi i client (VS Code e VS 2026). Tutto il
resto è negoziabile dentro un piano in [`.specify/plans/`](../plans/).

## 1. Parità funzionale tra client

Ogni feature deve atterrare **sia** su `src/vscode/` **sia** su `src/vs2026/`.
Eccezioni:

- ammesse solo se un blocco oggettivo (API piattaforma assente, vincolo SDK)
  è documentato nella sezione **"Cross-client parity"** del piano;
- richiedono conferma esplicita dell'autore prima dell'implementazione;
- devono indicare se/quando la parità verrà ristabilita.

## 2. Sicurezza & segreti

- Niente PAT/Bearer/segreti **nei log, nei messaggi UI, nei test**.
- Le credenziali passano sempre da:
  - VS Code: `AuthService` ([authService.ts](../../src/vscode/src/authService.ts));
  - VS 2026: `AdoAuthService` ([Auth/AdoAuthService.cs](../../src/vs2026/Auth/AdoAuthService.cs)).
- I 401/403 ADO devono passare dai flussi *unauthorized* esistenti, non
  generare popup ad-hoc.

## 3. Un solo stack HTTP per client

Tutte le chiamate ad Azure DevOps passano da:

- VS Code: `AdoClient` ([adoClient.ts](../../src/vscode/src/adoClient.ts));
- VS 2026: `AdoClient` ([AzureDevOps/AdoClient.cs](../../src/vs2026/AzureDevOps/AdoClient.cs)).

Nessun `fetch`/`HttpClient` parallelo, nessuna libreria HTTP aggiuntiva senza
piano approvato.

## 4. Localizzazione obbligatoria

Niente stringhe utente hard-coded.

- **VS Code**: `vscode.l10n.t(...)` con bundle in
  [`src/vscode/l10n/`](../../src/vscode/l10n/) e
  [`src/vscode/package.nls*.json`](../../src/vscode/package.nls.json).
- **VS 2026**: `Strings.<Key>` da
  [`Resources/Strings.resx`](../../src/vs2026/Resources/Strings.resx) (default)
  più tutti i `Strings.<culture>.resx` esistenti (it, fr, de, es, sv).

Aggiungere una chiave nuova **richiede** di aggiungerla in tutti i locale file
esistenti — anche solo copiando il valore inglese se la traduzione manca.

## 5. Logging

- Log via `LoggingService` (entrambi i client) con livelli `info`/`warn`/`error`.
- Niente `console.log` / `Debug.WriteLine` / `Console.WriteLine` in codice
  shipping.
- I log non devono contenere segreti, header `Authorization`, URL con PAT.

## 6. Performance & I/O ADO

- Tutte le chiamate ADO ripetute su collezioni vanno **concorrenti con cap**
  (vedi `mapWithConcurrency` in VS Code, `Chunk(..., 8)` in VS 2026).
- Operazioni lunghe devono essere **annullabili** (`CancellationToken` /
  `vscode.CancellationToken` o token interno).
- Niente caricamenti che bloccano la UI; il modello è lazy by default. Ogni
  deroga al lazy load va giustificata nel piano.

## 7. Test & validazione

Ogni piano deve dichiarare almeno:

- come si è validata la build (`npm run compile`, `dotnet build`);
- una checklist di **smoke manuale** per i due client;
- nuovi test automatici quando il cambiamento ha logica non triviale (parser,
  filtri, risoluzione path, ecc.).

## 8. Versioning — source of truth

- VS Code: `version` in
  [`src/vscode/package.json`](../../src/vscode/package.json).
- VS 2026: `Identity/@Version` in
  [`src/vs2026/source.extension.vsixmanifest`](../../src/vs2026/source.extension.vsixmanifest).

Bump versione, tag, push o workflow di release **solo** se il piano lo
prescrive esplicitamente e l'autore conferma. La matrice "Release decision
policy" in
[`.github/instructions/project.instructions.md`](../../.github/instructions/project.instructions.md)
resta autoritativa per decidere se un cambiamento merita un bump.

## 9. Scope discipline

- Niente refactor opportunistici fuori scope del piano.
- Niente nuove dipendenze runtime senza dichiararle e giustificarle nel piano.
- Niente cambi di formattazione su codice non toccato dalla feature.
