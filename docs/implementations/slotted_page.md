# Slotted page

Implementazione: `src/storage/page.rs`.

## Scelte

| Problema | Soluzione |
| --- | --- |
| **Struttura del progetto** | Un crate Cargo `handmade_db` con libreria (`src/lib.rs`) e binario demo (`src/main.rs`). |
| **Rappresentazione in memoria** | `Page` contiene un buffer `Box<[u8; 8192]>`; header e slot si leggono e scrivono direttamente nei byte. In RAM la pagina è identica a ciò che va su disco: nessuna serializzazione. |
| **Endianness** | Tutti i numeri sono little-endian. |
| **Header (12 byte)** | `page_id: u32` · `next_page_id: u32` · `num_slots: u16` · `free_space_end: u16`. |
| **Fine della lista collegata** | `next_page_id = u32::MAX` su disco; nell'API diventa `Option<PageId>` (`None`). Lo 0 resta un id valido. |
| **Slot (4 byte)** | `offset: u16` · `length: u16`. La directory cresce dopo l'header, i record dalla fine della pagina verso l'inizio. |
| **Cancellazione** | Non implementata nella versione 0 (servono solo inserimento e lettura). |
| **Validazione da disco** | `Page::from_bytes` verifica header e slot e restituisce `PageError::Corrupted` se i byte non sono coerenti. |

## Layout

```text
 0         4              8           10               12
 | page_id | next_page_id | num_slots | free_space_end | slot0 | slot1 | ... -->
                         ... spazio libero ...
                              <-- ... | record2 | record1 | record0 | 8192
```

- Spazio libero = `free_space_end - (12 + num_slots * 4)`.
- Inserire un record di `n` byte richiede `n + 4` byte liberi.
- Record massimo: `8192 - 12 - 4 = 8176` byte.
