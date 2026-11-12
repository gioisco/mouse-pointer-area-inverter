# Changelog

Le novità, le verifiche e i limiti delle versioni pubblicate di Mouse Pointer Area Inverter.

## [0.1.0] - 2026-11-12

Prima release pubblica: un puntatore che cambia colore come un'unica figura,
in base allo sfondo sotto la sua sagoma. Il comportamento è ora disponibile
in un'app portabile da usare senza tenere aperto un terminale.

### Funzioni

- **Inversione coerente dell'intero cursore.** La decisione considera lo sfondo
  sotto i pixel visibili della forma, rispettando trasparenza e punto di clic.
- **Undici forme standard statiche:** freccia, testo, croce, freccia verso l'alto,
  quattro ridimensionamenti, spostamento, divieto e mano/link.
- **Movimento e scia nativi.** Windows continua a gestirli; la scia può essere
  attiva o disattivata. I file dei cursori di Windows non vengono modificati.
- **Icona nella tray** con Pausa, Riprendi ed Esci. Pausa e uscita normale
  ripristinano le copie originali; il doppio clic alterna pausa e ripresa.
- **Esecuzione continua**, senza CSV o accumulo di statistiche nell'uso normale.
  Una sola istanza può modificare i cursori alla volta.
- **Pacchetto Windows x64 portabile**, con versione nell'eseguibile, istruzioni,
  changelog e tavolozza offline. Non richiede installazione o privilegi di amministratore.

### Taratura e risposta

- **Soglia di luminosità calibrata a 168/255**, anziché 220: il cursore passa al
  nero anche sui beige e sui grigi chiari indicati nelle prove visive.
  La taratura soddisfa i 17 cambi richiesti e conserva le altre 19 scelte della tavolozza.
- **Isteresi sulla copertura della sagoma:** inversione dal 75% di sfondo chiaro,
  ritorno al normale al 55% o meno, per limitare i cambi ripetuti su testo e bordi.
- **Campionamento ogni circa 100 ms.** Cambio al primo campione sulle aree quasi
  interamente chiare o non chiare; due conferme sugli sfondi misti.
- **Tavolozza con 36 sfondi e due anteprime per colore.** Mostra la scelta prevista
  e raccoglie solo i cambi desiderati in un riepilogo da copiare.
- **Icone della tray leggibili:** freccia ad alto contrasto quando attiva,
  due barre ambra quando in pausa.

### Verifiche e distribuzione

- Self-test per inversione, trasparenza, maschere, soglie, preferenze raccolte e icone incorporate.
- Controllo delle undici forme su bianco/scuro e del ripristino con scia attiva.
- Prova del menu Pausa/Riprendi/Esci e controllo esterno dell'eseguibile reale,
  inclusa la nuova taratura su beige, grigio 160 e grigio 176.
- Build e self-test su GitHub Actions; release costruita dal tag, con ZIP e checksum SHA-256.

### Limiti noti

- Cursori animati e cursori privati delle applicazioni non sono supportati.
- Prima di cambiare tema o dimensione del puntatore, mettere in pausa; riprendere dopo il cambio.
- **Recupero automatico dopo crash o chiusura forzata ancora assente.** Se il
  cursore resta alterato, chiudere l'app e riselezionare lo schema dei puntatori
  nelle impostazioni Mouse di Windows.
- **Il benchmark completo della versione con tray e soglia 168 resta da eseguire.**
  Le misure precedenti del programma diagnostico a 100 ms hanno rilevato un
  incremento medio di CPU programma + compositore dello 0,16% da fermo e 0,20%
  in movimento sul PC di sviluppo. Non certificano il consumo di questa release
  o quello su altri PC, né equivalenza GPU o latenza fisica dello schermo.
- Uso prolungato, più monitor e configurazioni DPI diverse richiedono ulteriori verifiche.
- L'eseguibile non è firmato. Sono richiesti Windows 10/11 x64 e .NET Framework 4.8.

[0.1.0]: https://github.com/gioisco/mouse-pointer-area-inverter/releases/tag/v0.1.0
