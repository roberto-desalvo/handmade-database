//! Livello di memorizzazione: come i dati sono disposti in byte, in memoria e
//! su disco.
//!
//! Per ora contiene solo la pagina (slotted page). In seguito ospiterà il
//! gestore del file (pager), il formato dei record, ecc.

pub mod page;

// `pub use` "ri-esporta" i tipi: chi usa la libreria può scrivere
// `handmade_db::storage::Page` invece di `handmade_db::storage::page::Page`.
pub use page::{PAGE_SIZE, Page, PageError, PageId, SlotId};
