//! # Slotted page
//!
//! Il file del database è diviso in pagine da 8 KB. Ogni pagina contiene più
//! record di lunghezza variabile, organizzati secondo lo schema "slotted page":
//!
//! ```text
//!  0         4              8           10               12
//!  +---------+--------------+-----------+----------------+-------+-------+----
//!  | page_id | next_page_id | num_slots | free_space_end | slot0 | slot1 | ...  -->
//!  +---------+--------------+-----------+----------------+-------+-------+----
//!  |                                                                          |
//!  |                          ... spazio libero ...                           |
//!  |                                                                          |
//!  |                                    <-- ... | record2 | record1 | record0 |
//!  +--------------------------------------------------------------------------+ 8192
//! ```
//!
//! - L'**header** (12 byte) sta all'inizio della pagina.
//! - La **directory degli slot** segue l'header e cresce verso il fondo.
//!   Ogni slot (4 byte) contiene `offset: u16` e `length: u16` del record.
//! - I **record** vengono scritti a partire dalla fine della pagina e
//!   crescono verso l'inizio.
//! - Lo spazio libero è quello compreso fra la fine della directory e
//!   `free_space_end` (l'inizio del record più recente).
//!
//! Il vantaggio: un record si identifica con il suo numero di slot, che non
//! cambia anche se in futuro il record verrà spostato dentro la pagina (ad
//! esempio durante una compattazione). Basterà aggiornare l'offset nello slot.
//!
//! Tutti i numeri sono memorizzati in **little-endian**.
//!
//! La pagina in memoria è *esattamente* il buffer di byte che finisce su disco:
//! non c'è un passaggio di serializzazione. Header e slot si leggono e
//! scrivono direttamente dentro il buffer.

// `use` importa nomi da altri moduli per poterli usare senza il percorso
// completo. `std` è la libreria standard di Rust.
use std::fmt;

// ---------------------------------------------------------------------------
// Costanti e tipi pubblici
// ---------------------------------------------------------------------------

/// Dimensione di una pagina in byte (8 KB), come da requisiti.
///
/// `const` definisce una costante valutata a tempo di compilazione.
/// `usize` è l'intero senza segno grande quanto un puntatore (64 bit su una
/// macchina a 64 bit): è il tipo che Rust usa per indici e lunghezze.
pub const PAGE_SIZE: usize = 8192;

/// Identificativo di una pagina: la sua posizione nel file (pagina 0, 1, 2...).
///
/// `type` crea un *alias*: `PageId` e `u32` sono lo stesso tipo, ma il nome
/// rende più chiaro il significato del valore nelle firme delle funzioni.
pub type PageId = u32;

/// Numero dello slot di un record dentro la pagina.
pub type SlotId = u16;

/// Dimensione dell'header della pagina in byte.
pub const HEADER_SIZE: usize = 12;

/// Dimensione di un singolo slot della directory: `offset: u16` + `length: u16`.
pub const SLOT_SIZE: usize = 4;

/// Record più grande che può stare in una pagina vuota: tutta la pagina meno
/// l'header e lo slot necessario a indicizzarlo.
pub const MAX_RECORD_SIZE: usize = PAGE_SIZE - HEADER_SIZE - SLOT_SIZE;

// ---------------------------------------------------------------------------
// Layout interno dell'header (costanti private: senza `pub` sono visibili
// solo in questo modulo)
// ---------------------------------------------------------------------------

/// Posizione del campo `page_id` (u32) nell'header.
const PAGE_ID_OFFSET: usize = 0;
/// Posizione del campo `next_page_id` (u32) nell'header.
const NEXT_PAGE_ID_OFFSET: usize = 4;
/// Posizione del campo `num_slots` (u16) nell'header.
const NUM_SLOTS_OFFSET: usize = 8;
/// Posizione del campo `free_space_end` (u16) nell'header.
const FREE_SPACE_END_OFFSET: usize = 10;

/// Valore scritto su disco in `next_page_id` quando la pagina è l'ultima della
/// lista collegata. Usiamo `u32::MAX` perché 0 è un id di pagina valido.
/// Nell'API pubblica questo valore non compare: si usa `Option<PageId>`.
const NO_NEXT_PAGE: u32 = u32::MAX;

// ---------------------------------------------------------------------------
// Errori
// ---------------------------------------------------------------------------

/// Errori che possono verificarsi operando su una pagina.
///
/// In Rust gli errori "attesi" non si gestiscono con eccezioni ma restituendo
/// un `Result<T, E>`: `Ok(valore)` in caso di successo, `Err(errore)` in caso
/// di fallimento. Il chiamante è obbligato dal compilatore a considerare
/// entrambi i casi.
///
/// `enum` in Rust è un tipo somma: un valore è *una* delle varianti, e ogni
/// variante può portare con sé dei dati (qui con campi nominati, `{ ... }`).
///
/// `#[derive(...)]` chiede al compilatore di generare automaticamente
/// l'implementazione di alcuni *trait* (interfacce):
/// - `Debug`: stampa per il debug con `{:?}`;
/// - `Clone`: copia esplicita con `.clone()`;
/// - `PartialEq`, `Eq`: confronto con `==` (usato nei test).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PageError {
    /// Il record è più grande di quanto possa mai stare in una pagina.
    RecordTooLarge { size: usize, max: usize },
    /// Il record non entra nello spazio libero rimasto in questa pagina.
    /// `needed` include anche lo slot da aggiungere alla directory.
    PageFull { needed: usize, available: usize },
    /// I byte letti non descrivono una pagina valida (es. file corrotto).
    /// `&'static str` è un riferimento a una stringa che vive per tutto il
    /// programma, come le stringhe letterali scritte nel codice.
    Corrupted(&'static str),
}

// `impl Trait for Tipo` implementa un'interfaccia per un tipo.
// `fmt::Display` definisce come il valore si stampa con `{}`: è il messaggio
// pensato per l'utente, a differenza di `Debug`.
impl fmt::Display for PageError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        // `match` confronta il valore con ogni variante. Il compilatore
        // verifica che tutte le varianti siano gestite (match esaustivo).
        // `PageError::RecordTooLarge { size, max }` "destruttura" la variante
        // estraendo i campi in variabili locali con lo stesso nome.
        match self {
            PageError::RecordTooLarge { size, max } => {
                write!(f, "record di {size} byte troppo grande (massimo {max})")
            }
            PageError::PageFull { needed, available } => write!(
                f,
                "spazio insufficiente nella pagina: servono {needed} byte, liberi {available}"
            ),
            PageError::Corrupted(reason) => write!(f, "pagina corrotta: {reason}"),
        }
    }
}

// `std::error::Error` è il trait standard degli errori. Ha solo metodi con
// un'implementazione di default, quindi il blocco `impl` può restare vuoto.
// Implementarlo permette di usare `PageError` con `?` dentro funzioni che
// restituiscono `Box<dyn Error>`, come il `main` del binario demo.
impl std::error::Error for PageError {}

// ---------------------------------------------------------------------------
// La pagina
// ---------------------------------------------------------------------------

/// Una pagina da 8 KB organizzata come slotted page.
///
/// `struct` definisce un tipo con campi. Il campo `data` è privato (manca
/// `pub`): chi usa `Page` non può toccare i byte direttamente, ma solo
/// tramite i metodi, che mantengono la pagina coerente.
pub struct Page {
    /// I byte della pagina.
    ///
    /// `[u8; PAGE_SIZE]` è un array di dimensione fissa, nota a tempo di
    /// compilazione. `Box<...>` lo alloca sull'heap invece che sullo stack:
    /// così spostare una `Page` (ad esempio restituirla da una funzione)
    /// copia solo un puntatore e non 8 KB.
    data: Box<[u8; PAGE_SIZE]>,
}

// Il blocco `impl Page` contiene i metodi e le funzioni associate al tipo.
impl Page {
    /// Crea una pagina vuota con l'id indicato.
    ///
    /// Funzione "associata" (non ha `self`): si chiama con `Page::new(3)`.
    /// `Self` è un alias per il tipo su cui stiamo implementando, qui `Page`.
    pub fn new(page_id: PageId) -> Self {
        // `[0u8; PAGE_SIZE]` crea un array di PAGE_SIZE byte tutti a zero.
        // `let mut` dichiara una variabile modificabile: in Rust le variabili
        // sono immutabili di default.
        let mut page = Self {
            data: Box::new([0u8; PAGE_SIZE]),
        };
        page.write_u32(PAGE_ID_OFFSET, page_id);
        page.write_u32(NEXT_PAGE_ID_OFFSET, NO_NEXT_PAGE);
        page.write_u16(NUM_SLOTS_OFFSET, 0);
        // All'inizio non ci sono record: lo spazio dei record "inizia" alla
        // fine della pagina. `as u16` è una conversione esplicita fra tipi
        // numerici (8192 sta comodamente in un u16, il cui massimo è 65535).
        page.write_u16(FREE_SPACE_END_OFFSET, PAGE_SIZE as u16);
        // L'ultima espressione di un blocco, senza `;`, è il valore restituito.
        page
    }

    /// Ricostruisce una pagina dai byte letti da disco, verificandone la
    /// coerenza.
    ///
    /// Prende possesso (*ownership*) del buffer: dopo la chiamata il buffer
    /// appartiene alla `Page` e il chiamante non può più usarlo. Così non
    /// serve copiare gli 8 KB.
    pub fn from_bytes(data: Box<[u8; PAGE_SIZE]>) -> Result<Self, PageError> {
        let page = Self { data };

        let free_space_end = page.free_space_end();
        if free_space_end > PAGE_SIZE {
            // `return` esce subito dalla funzione con il valore indicato.
            return Err(PageError::Corrupted("free_space_end oltre la fine della pagina"));
        }
        if page.slot_directory_end() > free_space_end {
            return Err(PageError::Corrupted(
                "la directory degli slot si sovrappone all'area dei record",
            ));
        }

        // Ogni slot deve puntare a un record interamente contenuto nell'area
        // dei record. Dopo questo controllo, `get` può indicizzare il buffer
        // senza rischiare un accesso fuori dai limiti.
        // `0..n` è un *range* (intervallo semiaperto: da 0 incluso a n escluso).
        for slot_id in 0..page.num_slots() {
            let (offset, length) = page.read_slot(slot_id);
            if offset < free_space_end || offset + length > PAGE_SIZE {
                return Err(PageError::Corrupted("slot che punta fuori dall'area dei record"));
            }
        }

        Ok(page)
    }

    /// Restituisce i byte della pagina, pronti per essere scritti su disco.
    ///
    /// `&self` significa che il metodo prende in prestito la pagina in sola
    /// lettura. Anche il valore restituito è un riferimento (`&`): il
    /// chiamante può leggere i byte senza copiarli, e il compilatore
    /// garantisce che non li usi dopo che la pagina è stata distrutta.
    pub fn as_bytes(&self) -> &[u8; PAGE_SIZE] {
        // `&self.data` è un `&Box<[u8; N]>`; Rust lo converte automaticamente
        // in `&[u8; N]` (deref coercion), perché `Box` si comporta come un
        // puntatore al suo contenuto.
        &self.data
    }

    // ----- Header ----------------------------------------------------------

    /// Id della pagina (la sua posizione nel file).
    pub fn page_id(&self) -> PageId {
        self.read_u32(PAGE_ID_OFFSET)
    }

    /// Pagina successiva nella lista collegata delle pagine della tabella.
    ///
    /// `Option<T>` rappresenta un valore che può mancare: `Some(valore)`
    /// oppure `None`. Rust non ha `null`: l'assenza è esplicita nel tipo.
    pub fn next_page_id(&self) -> Option<PageId> {
        match self.read_u32(NEXT_PAGE_ID_OFFSET) {
            NO_NEXT_PAGE => None,
            id => Some(id),
        }
    }

    /// Imposta (o rimuove, con `None`) la pagina successiva.
    ///
    /// `&mut self`: il metodo prende in prestito la pagina in modo *esclusivo*
    /// e può modificarla. Finché dura il prestito nessun altro può leggerla.
    pub fn set_next_page_id(&mut self, next: Option<PageId>) {
        // `unwrap_or` restituisce il valore contenuto in `Some`, oppure il
        // valore di riserva indicato se l'Option è `None`.
        self.write_u32(NEXT_PAGE_ID_OFFSET, next.unwrap_or(NO_NEXT_PAGE));
    }

    /// Numero di slot (e quindi di record) presenti nella pagina.
    pub fn num_slots(&self) -> u16 {
        self.read_u16(NUM_SLOTS_OFFSET)
    }

    /// Byte liberi fra la fine della directory degli slot e l'inizio dei
    /// record.
    ///
    /// Attenzione: per inserire un record di `n` byte servono `n + SLOT_SIZE`
    /// byte liberi, perché va aggiunto anche lo slot.
    pub fn free_space(&self) -> usize {
        self.free_space_end() - self.slot_directory_end()
    }

    // ----- Record ----------------------------------------------------------

    /// Inserisce un record nella pagina e restituisce il numero di slot
    /// assegnato.
    ///
    /// La pagina tratta il record come una sequenza opaca di byte: il suo
    /// formato interno (bitmap dei NULL, campi fissi, offset dei variabili)
    /// è compito di un altro livello.
    ///
    /// `&[u8]` è una *slice*: un riferimento a una sequenza contigua di byte
    /// di lunghezza nota a runtime. Accetta array, `Vec<u8>`, porzioni di
    /// buffer, ecc. senza copiarli.
    pub fn insert(&mut self, record: &[u8]) -> Result<SlotId, PageError> {
        let size = record.len();
        if size > MAX_RECORD_SIZE {
            return Err(PageError::RecordTooLarge {
                size,
                max: MAX_RECORD_SIZE,
            });
        }

        let needed = size + SLOT_SIZE;
        let available = self.free_space();
        if needed > available {
            // Quando un campo e una variabile hanno lo stesso nome si può
            // scrivere solo `needed` invece di `needed: needed`.
            return Err(PageError::PageFull { needed, available });
        }

        // 1. Copia il record subito prima dell'ultimo record inserito.
        let record_end = self.free_space_end();
        let record_start = record_end - size;
        // `self.data[a..b]` è la slice dei byte da `a` (incluso) a `b`
        // (escluso). `copy_from_slice` copia i byte di `record` al suo
        // interno; le due slice devono avere la stessa lunghezza, altrimenti
        // il programma va in panic (qui lo sono per costruzione).
        self.data[record_start..record_end].copy_from_slice(record);

        // 2. Aggiunge lo slot in fondo alla directory.
        let slot_id = self.num_slots();
        self.write_slot(slot_id, record_start, size);

        // 3. Aggiorna l'header.
        self.write_u16(NUM_SLOTS_OFFSET, slot_id + 1);
        self.write_u16(FREE_SPACE_END_OFFSET, record_start as u16);

        Ok(slot_id)
    }

    /// Legge il record nello slot indicato, se esiste.
    ///
    /// Restituisce un riferimento ai byte *dentro* la pagina, senza copiarli.
    /// Il compilatore lega la durata del riferimento a quella di `&self`:
    /// non è possibile tenere il record dopo aver modificato o distrutto la
    /// pagina (regole del *borrow checker*).
    pub fn get(&self, slot_id: SlotId) -> Option<&[u8]> {
        if slot_id >= self.num_slots() {
            return None;
        }
        let (offset, length) = self.read_slot(slot_id);
        Some(&self.data[offset..offset + length])
    }

    /// Itera su tutti i record della pagina in ordine di slot, restituendo
    /// coppie `(slot_id, record)`.
    ///
    /// `impl Iterator<Item = ...>` significa "un tipo che implementa
    /// `Iterator`", senza doverne scrivere il nome esatto (che sarebbe lungo
    /// e complicato). Il `'_` indica che l'iteratore prende in prestito la
    /// pagina, quindi non può sopravviverle.
    pub fn iter(&self) -> impl Iterator<Item = (SlotId, &[u8])> + '_ {
        // `map` trasforma ogni elemento del range in una coppia.
        // `move |slot_id| ...` è una *closure* (funzione anonima); `move` fa
        // sì che catturi `self` (un riferimento) per valore, così la closure
        // può essere restituita insieme all'iteratore.
        // Senza cancellazioni ogni slot contiene un record, quindi `get` non
        // può restituire `None`: `expect` lo verifica e, se fosse violato,
        // interromperebbe il programma con il messaggio indicato.
        (0..self.num_slots()).map(move |slot_id| {
            let record = self.get(slot_id).expect("slot entro num_slots");
            (slot_id, record)
        })
    }

    // ----- Funzioni di supporto private ------------------------------------

    /// Offset del primo byte occupato dai record.
    fn free_space_end(&self) -> usize {
        // `usize::from` converte un u16 in usize. Non può fallire (usize è
        // sempre almeno grande quanto u16), per questo esiste `from` e non
        // serve `as`.
        usize::from(self.read_u16(FREE_SPACE_END_OFFSET))
    }

    /// Offset del primo byte dopo la directory degli slot.
    fn slot_directory_end(&self) -> usize {
        HEADER_SIZE + usize::from(self.num_slots()) * SLOT_SIZE
    }

    /// Posizione nella pagina dello slot indicato.
    fn slot_position(slot_id: SlotId) -> usize {
        HEADER_SIZE + usize::from(slot_id) * SLOT_SIZE
    }

    /// Legge `(offset, length)` dallo slot indicato.
    ///
    /// Una funzione può restituire più valori raggruppandoli in una *tupla*.
    fn read_slot(&self, slot_id: SlotId) -> (usize, usize) {
        let pos = Self::slot_position(slot_id);
        let offset = usize::from(self.read_u16(pos));
        let length = usize::from(self.read_u16(pos + 2));
        (offset, length)
    }

    /// Scrive `(offset, length)` nello slot indicato.
    fn write_slot(&mut self, slot_id: SlotId, offset: usize, length: usize) {
        let pos = Self::slot_position(slot_id);
        self.write_u16(pos, offset as u16);
        self.write_u16(pos + 2, length as u16);
    }

    /// Legge un u16 little-endian alla posizione indicata.
    fn read_u16(&self, pos: usize) -> u16 {
        // `self.data[pos..pos + 2]` è una slice di 2 byte; `try_into()` la
        // converte in un array `[u8; 2]`. La conversione può fallire in
        // generale (se la slice avesse un'altra lunghezza), quindi restituisce
        // un `Result`; qui la lunghezza è sempre 2 e `unwrap()` non fallisce.
        let bytes: [u8; 2] = self.data[pos..pos + 2].try_into().unwrap();
        u16::from_le_bytes(bytes)
    }

    /// Scrive un u16 little-endian alla posizione indicata.
    fn write_u16(&mut self, pos: usize, value: u16) {
        self.data[pos..pos + 2].copy_from_slice(&value.to_le_bytes());
    }

    /// Legge un u32 little-endian alla posizione indicata.
    fn read_u32(&self, pos: usize) -> u32 {
        let bytes: [u8; 4] = self.data[pos..pos + 4].try_into().unwrap();
        u32::from_le_bytes(bytes)
    }

    /// Scrive un u32 little-endian alla posizione indicata.
    fn write_u32(&mut self, pos: usize, value: u32) {
        self.data[pos..pos + 4].copy_from_slice(&value.to_le_bytes());
    }
}

// ---------------------------------------------------------------------------
// Test
// ---------------------------------------------------------------------------

// `#[cfg(test)]` compila questo modulo solo quando si eseguono i test
// (`cargo test`), quindi non finisce nella libreria.
#[cfg(test)]
mod tests {
    // `super::*` importa tutto ciò che è definito nel modulo padre (questo
    // file), anche gli elementi privati: i test di unità possono verificare
    // i dettagli interni.
    use super::*;

    // `#[test]` marca una funzione come test. Il test fallisce se va in panic,
    // ad esempio quando un `assert_eq!` non è soddisfatto.
    #[test]
    fn new_page_is_empty() {
        let page = Page::new(7);
        assert_eq!(page.page_id(), 7);
        assert_eq!(page.next_page_id(), None);
        assert_eq!(page.num_slots(), 0);
        assert_eq!(page.free_space(), PAGE_SIZE - HEADER_SIZE);
        assert_eq!(page.get(0), None);
        assert_eq!(page.iter().count(), 0);
    }

    #[test]
    fn insert_and_get() {
        let mut page = Page::new(0);
        // `b"..."` è una stringa di byte: il suo tipo è `&[u8; N]`, che si
        // converte automaticamente in `&[u8]`.
        let a = page.insert(b"alfa").unwrap();
        let b = page.insert(b"beta-gamma").unwrap();

        assert_eq!(a, 0);
        assert_eq!(b, 1);
        // `Some(&b"alfa"[..])`: `[..]` trasforma l'array in una slice, per
        // confrontarla con il tipo restituito da `get`.
        assert_eq!(page.get(a), Some(&b"alfa"[..]));
        assert_eq!(page.get(b), Some(&b"beta-gamma"[..]));
        assert_eq!(page.get(2), None);
    }

    #[test]
    fn records_grow_from_the_end_of_the_page() {
        let mut page = Page::new(0);
        page.insert(b"12345").unwrap();
        page.insert(b"xyz").unwrap();

        assert_eq!(page.read_slot(0), (PAGE_SIZE - 5, 5));
        assert_eq!(page.read_slot(1), (PAGE_SIZE - 8, 3));
        assert_eq!(page.free_space_end(), PAGE_SIZE - 8);
        assert_eq!(page.free_space(), PAGE_SIZE - HEADER_SIZE - 2 * SLOT_SIZE - 8);
    }

    #[test]
    fn iter_returns_records_in_slot_order() {
        let mut page = Page::new(0);
        page.insert(b"uno").unwrap();
        page.insert(b"due").unwrap();
        page.insert(b"tre").unwrap();

        // `collect()` raccoglie gli elementi dell'iteratore in una collezione;
        // il tipo di destinazione (`Vec<_>`) si ricava dall'annotazione.
        // `_` lascia al compilatore il compito di dedurre il tipo interno.
        let records: Vec<_> = page.iter().collect();
        assert_eq!(
            records,
            vec![(0, &b"uno"[..]), (1, &b"due"[..]), (2, &b"tre"[..])]
        );
    }

    #[test]
    fn largest_record_fills_the_page_exactly() {
        let mut page = Page::new(0);
        // `vec![valore; n]` crea un Vec di n elementi uguali.
        let record = vec![0xAB; MAX_RECORD_SIZE];
        page.insert(&record).unwrap();

        assert_eq!(page.free_space(), 0);
        assert_eq!(page.get(0), Some(&record[..]));
        assert_eq!(
            page.insert(b""),
            Err(PageError::PageFull {
                needed: SLOT_SIZE,
                available: 0
            })
        );
    }

    #[test]
    fn record_too_large_is_rejected() {
        let mut page = Page::new(0);
        let record = vec![0; MAX_RECORD_SIZE + 1];
        assert_eq!(
            page.insert(&record),
            Err(PageError::RecordTooLarge {
                size: MAX_RECORD_SIZE + 1,
                max: MAX_RECORD_SIZE
            })
        );
        // Un inserimento fallito non deve modificare la pagina.
        assert_eq!(page.num_slots(), 0);
    }

    #[test]
    fn insert_fails_when_page_is_full() {
        let mut page = Page::new(0);
        let record = [1u8; 1000];
        let mut inserted = 0;
        // Ripete finché `insert` riesce: `is_ok()` vale `true` se il
        // `Result` è `Ok`.
        while page.insert(&record).is_ok() {
            inserted += 1;
        }
        // Ogni record occupa 1000 + 4 byte: in 8180 byte liberi ne entrano 8.
        assert_eq!(inserted, 8);
        assert_eq!(page.num_slots(), 8);
        // Un record più piccolo può ancora entrare nello spazio rimasto.
        let small = vec![2u8; page.free_space() - SLOT_SIZE];
        assert!(page.insert(&small).is_ok());
        assert_eq!(page.free_space(), 0);
    }

    #[test]
    fn next_page_id_round_trip() {
        let mut page = Page::new(0);
        page.set_next_page_id(Some(42));
        assert_eq!(page.next_page_id(), Some(42));
        page.set_next_page_id(None);
        assert_eq!(page.next_page_id(), None);
    }

    #[test]
    fn bytes_round_trip() {
        let mut page = Page::new(3);
        page.set_next_page_id(Some(4));
        page.insert(b"persistente").unwrap();
        page.insert(b"anche io").unwrap();

        // Simula scrittura e rilettura da disco: copia i byte in un nuovo
        // buffer e ricostruisce la pagina.
        let copy = Box::new(*page.as_bytes());
        let restored = Page::from_bytes(copy).unwrap();

        assert_eq!(restored.page_id(), 3);
        assert_eq!(restored.next_page_id(), Some(4));
        assert_eq!(restored.get(0), Some(&b"persistente"[..]));
        assert_eq!(restored.get(1), Some(&b"anche io"[..]));
        assert_eq!(restored.free_space(), page.free_space());
    }

    #[test]
    fn from_bytes_rejects_corrupted_pages() {
        // free_space_end oltre la fine della pagina.
        let mut page = Page::new(0);
        page.write_u16(FREE_SPACE_END_OFFSET, (PAGE_SIZE + 1) as u16);
        // `matches!` verifica che un valore corrisponda a un pattern.
        assert!(matches!(
            Page::from_bytes(page.data),
            Err(PageError::Corrupted(_))
        ));

        // Directory degli slot che invade l'area dei record.
        let mut page = Page::new(0);
        page.insert(b"x").unwrap();
        page.write_u16(NUM_SLOTS_OFFSET, 5000);
        assert!(matches!(
            Page::from_bytes(page.data),
            Err(PageError::Corrupted(_))
        ));

        // Slot che punta oltre la fine della pagina.
        let mut page = Page::new(0);
        page.insert(b"x").unwrap();
        page.write_slot(0, PAGE_SIZE - 1, 10);
        assert!(matches!(
            Page::from_bytes(page.data),
            Err(PageError::Corrupted(_))
        ));
    }
}
