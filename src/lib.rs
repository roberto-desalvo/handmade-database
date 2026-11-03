//! # handmade_db
//!
//! Database didattico scritto in Rust.
//!
//! I commenti che iniziano con `//!` documentano l'elemento che li *contiene*
//! (qui l'intero crate), mentre quelli con `///` documentano l'elemento che
//! *segue*. Entrambi finiscono nella documentazione generata da `cargo doc`.
//!
//! Il crate è una libreria (`src/lib.rs`): espone funzioni e tipi che altri
//! programmi possono usare. Il binario demo in `src/main.rs` è uno di questi.

// `pub mod storage;` dichiara il modulo `storage` e lo rende pubblico.
// Rust cerca il suo codice in `src/storage/mod.rs` (oppure `src/storage.rs`).
// Senza `pub` il modulo sarebbe visibile solo dentro questo crate.
pub mod storage;
