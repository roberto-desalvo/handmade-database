# RDS.Database — Funzionalità implementate

Panoramica dello stato attuale del motore di database (`RDS.Database.Console`), aggiornata al 2026-08-26.

## Storage engine

- **Pager** (`Services/Pager.cs`): gestisce l'accesso a un file su disco come sequenza di pagine di dimensione fissa (4096 byte).
  - Apertura/creazione del file del database (`FileMode.OpenOrCreate`).
  - Lettura (`Read`) e scrittura (`Write`) random-access di una pagina per indice.
  - Allocazione di nuove pagine in coda al file (`AllocateNewPage`).
  - Calcolo del numero di pagine presenti (`PagesCount`) a partire dalla dimensione del file.
  - Sincronizzazione esplicita su disco (`Sync`, flush).
  - Validazioni: percorso file obbligatorio, dimensione del file multiplo della dimensione di pagina, dimensione del buffer passato coerente con `PAGE_SIZE`.

- **Page — formato "slotted page"** (`Services/Page.cs`): rappresenta il contenuto di una singola pagina come `ref struct` sopra uno `Span<byte>`.
  - Header di pagina: tipo pagina (dati/indice), numero di record, offset del prossimo record libero, indice della pagina successiva nella catena.
  - Slot directory: per ogni record, offset e dimensione all'interno della pagina.
  - **Insert**: inserimento di un record di lunghezza variabile, con verifica dello spazio libero contiguo e della dimensione massima consentita per singolo record; ritorna `-1` se la pagina non ha più spazio.
  - **Read**: lettura di un record tramite indice di slot.
  - **Delete**: cancellazione logica di un record (marcatura della dimensione a zero) e verifica dello stato (`IsDeleted`).
  - **TryUpdate**: aggiornamento di un record esistente
    - in-place se la nuova dimensione è minore o uguale a quella esistente;
    - spostamento all'interno della stessa pagina se la nuova dimensione è maggiore ma c'è spazio libero (anche recuperato tramite compattazione) sufficiente;
    - fallimento (`false`) se non c'è spazio nemmeno dopo la compattazione, lasciando il record originale intatto.
  - **Forwarding**: quando un record non entra più nella pagina originale, può essere marcato come "spostato" (`MarkAsForwarded`) con un puntatore (pagina, slot) alla nuova posizione, letto poi tramite `ReadForwarding`/`IsForwarding`. Se il record non ha abbastanza byte per contenere il puntatore, la pagina prova a compattarsi prima di rinunciare.
  - **Compattazione della pagina** (`Compact`/`TryCompact`): ricompatta i record vivi eliminando i "buchi" lasciati da record cancellati o spostati, aggiornando slot directory e confine dello spazio libero. `TryCompact` verifica prima (`GetTotalFreeSpace`) se la compattazione libererebbe spazio a sufficienza, evitando di eseguirla quando non servirebbe. Viene invocata automaticamente da `Insert`, `TryUpdate` e `MarkAsForwarded` quando lo spazio libero contiguo non basta ma quello complessivo (contando anche i record cancellati) sì.
  - Concatenamento di pagine tramite `GetNextPageIndex`/`SetNextPageIndex`, usato per collegare più pagine dati in un'unica tabella.

## Livello tabella

- **Table** (`Services/Table.cs`): astrazione sopra una catena di pagine dati collegate.
  - **Insert**: inserisce un record nell'ultima pagina della catena; se non c'è spazio, alloca automaticamente una nuova pagina e vi inserisce il record, aggiornando il collegamento tra pagine.
  - **Read**: legge un record dato un `RecordIdentifier` (pagina + slot), risolvendo automaticamente un livello di forwarding se il record è stato spostato.
  - **Delete**: cancella logicamente un record dato il suo identificatore.
  - **Update**: aggiorna un record; se non entra più nella pagina originale, cerca spazio nelle pagine della tabella (allocandone una nuova se necessario) e marca il record originale come forwarding verso la nuova posizione.
  - **Scan**: restituisce un `TableScanner` per l'attraversamento sequenziale di tutti i record validi della tabella.

- **TableScanner** (`Services/TableScanner.cs`): cursore forward-only che attraversa tutte le pagine della catena in ordine, saltando automaticamente i record cancellati e quelli marcati come forwarding (che vengono comunque incontrati, in ordine diverso, nella loro posizione reale).

## Livello database

- **Database** (`Services/Database.cs`): punto di ingresso che rappresenta un intero file di database.
  - Alla creazione di un nuovo file: inizializza la pagina 0 con un *magic number* di riconoscimento del formato e il riferimento alla prima pagina della tabella; alloca e inizializza la prima pagina dati della tabella.
  - All'apertura di un file esistente: valida l'integrità dell'header (magic number, indice della tabella nei limiti del file, pagina della tabella effettivamente inizializzata) prima di esporre la tabella.
  - Espone `Table` come unica tabella del database.
  - `Sync`/`Dispose` per la persistenza e il rilascio delle risorse.

## Utility e validazioni trasversali

- `Constants`: dimensione di pagina, dimensione header/slot, magic number, marcatore di forwarding, dimensione massima di un singolo record.
- `Guard`: validazioni riutilizzate (dimensione buffer di pagina, valori non negativi/positivi, dimensione massima record).
- `Utils`: helper per leggere una pagina dal `Pager` e ottenerne la vista come `Page`.

## Cosa manca / non ancora implementato

- Il programma console (`Program.cs`) non usa ancora il motore: è solo uno scheletro ("Hello, World!").
- Nessun linguaggio di query: l'accesso è solo tramite API `Table` (Insert/Read/Update/Delete/Scan).
- Nessun indice: `PAGE_TYPE_INDEX` è definito ma non è ancora usato nessun meccanismo di indicizzazione (B-Tree o altro).
- Nessuna transazionalità/WAL, nessun supporto multi-tabella, nessuna concorrenza multi-thread/multi-processo (il file viene aperto in accesso esclusivo).
