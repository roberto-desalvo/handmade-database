# Database didattico: registro delle scelte

Nov 2, 2026 · @Roberto

Ogni capitolo è una versione del database; ogni sottocapitolo è una funzionalità di quella versione, con le scelte che richiede.

## Versione 0

### Definizione dei dati

Il database accetta solo righe con la forma dichiarata nello schema della tabella.

| Problema | Soluzione |
| --- | --- |
| **Tipi di dato** *Quali tipi accettiamo per le colonne.* | Supportiamo solo `int` e `varchar`. |
| **Schema e validazione** *Come si descrive la tabella e cosa rende una riga conforme.* | La tabella si descrive con uno schema fisso nel catalogo. Le righe vengono validate all'inserimento, confrontandole con il catalogo. |

### Persistenza

Le righe vengono scritte su disco e ritrovate dopo un riavvio.

| Problema | Soluzione |
| --- | --- |
| **Formato del record** *Come una riga diventa una sequenza di byte, e viceversa.* | • Disposizione: parte con i dati fissi, poi tabella degli offset dei dati variabili, poi i dati variabili.<br>• NULL: bitmap con un bit per colonna.<br>• `int`: 4 byte a larghezza fissa. |
| **Organizzazione del file** *Come i record sono disposti nel file.* | • File diviso in pagine da 8 KB.<br>• Record nella pagina: slotted page.<br>• Ordine dei record: heap.<br>• Pagine della tabella: lista collegata (sarebbe meglio una mappa, che scala meglio). |
| **Una o più tabelle** *Un file per tabella oppure un file unico.* | Un solo file per tutto il database, catalogo compreso. |
| **Dove vive lo schema** *Come il database, al riavvio, sa com'è fatta la tabella.* | Il catalogo è fatto di tabelle di sistema; quella principale sta in posizione fissa, all'inizio del file. Viene caricato in RAM all'avvio. |
| **Quando si scrive su disco** *A ogni inserimento o in un altro momento.* | A ogni modifica la pagina viene scritta su disco e sincronizzata. |

### Recupero dei dati

Le righe salvate possono essere lette.

| Problema | Soluzione |
| --- | --- |
| **Interfaccia** *Come si inseriscono e si recuperano i dati: tutte le righe oppure con un filtro.* | • Il database è una libreria che espone funzioni da chiamare.<br>• Funzioni: creare una tabella, inserire una riga, leggere le righe, tutte oppure filtrate. |