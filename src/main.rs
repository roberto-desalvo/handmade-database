//! Binario demo: mostra come usare la libreria `handmade_db`.
//!
//! Si esegue con `cargo run`. Un package Cargo può contenere sia una
//! libreria (`src/lib.rs`) sia un binario (`src/main.rs`); il binario usa la
//! libreria come se fosse una dipendenza esterna, tramite il nome del crate.

use handmade_db::storage::Page;

// `Box<dyn std::error::Error>` è "un qualsiasi tipo di errore": `dyn` indica
// un oggetto il cui tipo concreto è noto solo a runtime. Così `main` può
// propagare errori di tipi diversi con l'operatore `?`.
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut page = Page::new(0);
    println!("Pagina vuota: {} byte liberi", page.free_space());

    // `for x in [...]` itera sugli elementi di un array.
    for text in ["Ada Lovelace", "Alan Turing", "Grace Hopper"] {
        // `?` : se `insert` restituisce `Err`, `main` termina subito
        // restituendo quell'errore; se restituisce `Ok(v)`, l'espressione
        // vale `v`. `as_bytes()` su una stringa ne dà i byte UTF-8.
        let slot = page.insert(text.as_bytes())?;
        println!("Inserito {text:?} nello slot {slot}");
    }

    println!(
        "Dopo gli inserimenti: {} slot, {} byte liberi",
        page.num_slots(),
        page.free_space()
    );

    // Simula il ciclo scrittura/rilettura da disco.
    let restored = Page::from_bytes(Box::new(*page.as_bytes()))?;
    println!("Record nella pagina riletta:");
    for (slot, record) in restored.iter() {
        // `String::from_utf8_lossy` interpreta i byte come testo UTF-8.
        println!("  slot {slot}: {}", String::from_utf8_lossy(record));
    }

    Ok(())
}
