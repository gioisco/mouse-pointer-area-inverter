# Mouse Pointer Area Inverter

Inverte i colori dell'intero puntatore in base allo sfondo sotto la sua sagoma.
Windows continua a gestire movimento e scia, sia con la scia attiva sia senza.
Il programma rimane nell'area di notifica, vicino all'orologio.

## Requisiti

- Windows 10 o Windows 11, x64, con .NET Framework 4.8.
- Una sessione desktop interattiva. Non servono privilegi di amministratore.

Il pacchetto usa le librerie di Windows e non richiede SDK, .NET moderno o
dipendenze da scaricare. Lo sviluppo e le misure sono stati effettuati su un
singolo PC: il funzionamento e le prestazioni su altre configurazioni devono
ancora essere verificati.

## Installazione e avvio

1. Su GitHub apri **Actions → Windows portable build**, scegli un'esecuzione
   riuscita del ramo `master` e scarica l'artefatto
   **MousePointerAreaInverter-windows-x64**. Per scaricare gli artefatti puo
   essere necessario accedere a GitHub.
2. Estrai lo ZIP scaricato in una cartella a tua scelta.
3. Apri `MousePointerAreaInverter.exe`. Non compare una finestra o un terminale:
   cerca l'icona nell'area di notifica, eventualmente dentro la freccia delle
   icone nascoste vicino all'orologio.

Il programma non e firmato: Windows potrebbe mostrare un avviso relativo
all'autore sconosciuto. Controlla la provenienza del pacchetto prima di aprirlo.
Non viene installato un servizio e non viene configurato l'avvio automatico.

## Uso

Clic destro sull'icona:

- **Pausa** ripristina i cursori originali e ferma il campionamento.
- **Riprendi** acquisisce i cursori correnti e riattiva l'adattamento.
- **Esci** ripristina i cursori e chiude il programma.

Un doppio clic sull'icona alterna Pausa e Riprendi. E consentita una sola
istanza, condivisa anche con i programmi di prova del repository.
L'icona mostra un puntatore bianco con bordo nero quando attivo e due grandi
barre ambra quando sospeso.
L'esecuzione continua non scrive log o CSV e non accumula campioni in memoria.

Per cambiare tema o dimensione del puntatore, metti prima in pausa, modifica
le impostazioni di Windows e poi riprendi.

## Limiti attuali e recupero

Sono supportate undici forme standard statiche: freccia, testo, croce, freccia
su, quattro ridimensionamenti, spostamento, divieto e mano/link. I cursori
animati e quelli privati delle applicazioni non vengono modificati.
Il campionamento avviene ogni circa 100 ms; gli sfondi misti richiedono due
conferme per limitare i cambi ripetuti.

Pausa e uscita normale ripristinano le copie originali. Il recupero automatico
dopo crash o chiusura forzata non e ancora implementato: se il cursore resta
alterato, chiudi il programma e riseleziona lo schema dei puntatori in
**Impostazioni Mouse → Impostazioni aggiuntive del mouse → Puntatori → OK/Applica**.

La versione con icona deve ancora ripetere il confronto prestazionale completo.
I risultati precedenti e i relativi limiti sono descritti in `PERFORMANCE.txt`
nel repository; non costituiscono una garanzia dello stesso consumo su altri PC.

## Aggiornamento e rimozione

Scegli **Esci** prima di sostituire l'eseguibile con una nuova versione.
Per rimuovere il programma scegli **Esci** ed elimina la cartella estratta.
Non vengono modificati i file dei cursori di Windows o salvate preferenze
nel registro.

## Tavolozza di prova

Per provare il cursore su una gamma di sfondi apri `palette.html` dal repository nel browser,
con l'app attiva. I menu sotto i colori raccolgono le preferenze in un riepilogo
da copiare; la pagina non cambia le soglie dell'app e funziona offline.

## Compilazione dai sorgenti

Da PowerShell, nella cartella del repository:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Da Git Bash:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./build.ps1
```

Lo script usa il compilatore del .NET Framework di Windows, esegue i self-test
senza modificare i cursori e crea `dist/MousePointerAreaInverter.zip`.
La GitHub Action esegue lo stesso comando su un runner Windows e pubblica il
pacchetto come artefatto. I test visivi e i benchmark richiedono un desktop
reale e non vengono eseguiti in CI.

I vecchi comandi diagnostici, tra cui `adaptive.ps1`, `check-shapes.ps1` e
`performance-gate.ps1`, restano separati e possono muovere o modificare il
puntatore. Chiudi l'app con icona prima di avviarli.

`check-tray.ps1` verifica sul desktop reale l'inversione tramite il timer
dell'app, le voci Pausa/Riprendi/Esci, il ripristino e l'assenza di CSV.
Durante questa prova di circa 5 secondi non muovere il mouse e non cliccare.

`check-running-app.ps1` osserva invece l'eseguibile portabile gia avviato in
un processo separato: deve cambiare la freccia passando fra due sfondi, bianco
e scuro. Lascia il mouse fermo per circa 3 secondi. Con `-Observe` non muove
il puntatore e riporta per 6 secondi forma riconosciuta e copertura chiara.
