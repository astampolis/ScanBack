# ScanBack από Windows PC σε IIS

Επιλογή βάσης: **SQL Server 2022 Express** για μικρή εγκατάσταση, ή Standard/managed SQL όταν απαιτείται μεγαλύτερη χωρητικότητα. Η Express έχει όριο 10 GB ανά βάση και δεν περιλαμβάνει SQL Agent: προγραμμάτισε backups με Windows Task Scheduler. Η εφαρμογή χρησιμοποιεί ήδη SQL Server και ελεγμένα EF migrations· δεν χρειάζεται Supabase ή μεταφορά δεδομένων.

## 1. Δημιουργία πακέτου στο Windows PC

Εγκατάστησε Git, PowerShell 7 και το .NET SDK που ορίζει το `global.json` (10.0.402). Άνοιξε PowerShell 7:

```powershell
git clone https://github.com/astampolis/ScanBack.git
cd ScanBack
# Αν έχεις ήδη checkout: git pull --ff-only
.\scripts\publish-iis.ps1
```

Το `artifacts\iis\ScanBack-IIS-win-x64.zip` είναι το πλήρες πακέτο Web, Worker, Tools και εγκατάστασης. Το server δεν χρειάζεται SDK, Git, Node, Docker ή Visual Studio. Μετέφερε ZIP και `.sha256` και σύγκρινε το hash πριν την αποσυμπίεση:

```powershell
Get-FileHash .\ScanBack-IIS-win-x64.zip -Algorithm SHA256
Expand-Archive .\ScanBack-IIS-win-x64.zip C:\ScanBack-Package
```

## 2. Προαπαιτούμενα server (μία φορά)

Windows Server 2022/2025 x64. Το PC μπορεί να κάνει build και μεταφορά· ο υποστηριζόμενος production στόχος είναι Windows Server, όχι Windows Home.

Σε Windows PowerShell 5.1 ως Administrator:

```powershell
Install-WindowsFeature Web-Server,Web-Http-Redirect,Web-Mgmt-Console,Web-Scripting-Tools,Web-AppInit
```

Εγκατάστησε το .NET 10 x64 **Hosting Bundle μετά το IIS** και κάνε την απαιτούμενη επανεκκίνηση. Εγκατάστησε SQL Server Express ή άλλη κατάλληλη έκδοση και Microsoft `sqlcmd`, με Windows authentication. Η καθοδηγούμενη εγκατάσταση υποθέτει SQL και IIS στο ίδιο μηχάνημα και τα προεπιλεγμένα ονόματα/μονοπάτια. Για remote SQL ακολούθησε το `iis-deployment.md`.

Χρειάζονται δικές σου πραγματικές τιμές:

- DNS hostname που δείχνει στον server και δημόσιο HTTPS πιστοποιητικό με private key στο LocalMachine/My. Άνοιξε 80/443 στο firewall.
- SQL TCP endpoint με πιστοποιητικό που αντιστοιχεί στο SQL DNS name και είναι έμπιστο από τον server. Δεν χρησιμοποιείται `TrustServerCertificate=True`.
- SMTP λογαριασμός με verified sender και θύρα 587 ή 465.
- Στοιχεία υπευθύνου επεξεργασίας και έκδοση εγκεκριμένης πολιτικής απορρήτου.

Αυτά αφορούν τη δική σου υποδομή και δεν μπορούν να συμπεριληφθούν ως κοινά credentials στο ZIP.

## 3. Καθοδηγούμενη εγκατάσταση

Από το αποσυμπιεσμένο πακέτο, σε elevated **Windows PowerShell 5.1** (όχι PowerShell 7, επειδή τα IIS modules χρησιμοποιούν 5.1):

```powershell
cd C:\ScanBack-Package
.\deploy\Setup-ScanBack.ps1 -Phase All
```

Ο λογαριασμός εκτέλεσης χρειάζεται local Administrator και δικαιώματα δημιουργίας βάσης/logins στο τοπικό SQL. Ο οδηγός ζητά τις πραγματικές τιμές, κρύβει τα passwords μέσω Windows credential dialog, γράφει προστατευμένο εξωτερικό JSON, δημιουργεί ξεχωριστό RSA certificate προστασίας δεδομένων, εγκαθιστά IIS site και Windows Worker service, δημιουργεί τη ScanBack βάση αν απουσιάζει, δίνει μόνο DML στα runtime accounts, εκτελεί migrations/initialization και δημιουργεί admin. Στο τέλος εκτελεί έλεγχο HTTPS/database και ενεργοποιεί αυτόματη εκκίνηση.

Για χωριστούς λογαριασμούς εγκατάστασης/SQL operator ή διάγνωση εκτέλεσε τις φάσεις με τη σειρά:

```powershell
.\deploy\Setup-ScanBack.ps1 -Phase Configure
.\deploy\Setup-ScanBack.ps1 -Phase Install
.\deploy\Setup-ScanBack.ps1 -Phase Initialize
.\deploy\Setup-ScanBack.ps1 -Phase Start
```

Το `Initialize` απαιτεί elevated operator με πρόσβαση στο config, key ring, private key και SQL provisioning. Δεν δίνονται SQL admin δικαιώματα στο IIS ή στον Worker. Το υπάρχον config διατηρείται· για διόρθωση επεξεργάσου το `C:\ProgramData\ScanBack\config\appsettings.Production.json`. Μην επανεκτελείς `All` ή `Install` μετά από εγκατάσταση: για updates χρησιμοποίησε `Update-ScanBack.ps1`. Αν η εγκατάσταση αποτύχει στη μέση, ακολούθησε την αποκατάσταση της ενότητας 5 στο `iis-deployment.md`, διατηρώντας δεδομένα και κλειδιά.

## 4. Έλεγχος πριν την κανονική χρήση

Άνοιξε `https://ΤΟ-DOMAIN/health`, κάνε login και ενεργοποίησε TOTP MFA στο Account security για πρόσβαση στο `/admin`. Με δοκιμαστικά δεδομένα έλεγξε registration/email confirmation, reset password, emergency contact confirmation, QR και incident notification. Ο οδηγός δεν στέλνει email αυτόματα. Για σκόπιμη δοκιμή σε δική σου διεύθυνση:

```powershell
C:\inetpub\ScanBack\Tools\ScanBack.Deployment.exe smtp-test `
  --config C:\ProgramData\ScanBack\config\appsettings.Production.json `
  --to 'operations@TO-DOMAIN' --confirm-send
```

Πριν βάλεις πραγματικά δεδομένα κράτησε συντονισμένο backup SQL, photos, key ring και **private key του Data Protection certificate**, δοκίμασε restore και ρύθμισε ανανέωση HTTPS/monitoring. Οι λεπτομέρειες βρίσκονται στα `iis-deployment.md` και `operations.md`. Η επιτυχία build σε Linux δεν αντικαθιστά τον έλεγχο IIS/SQL/SMTP στο δικό σου Windows Server.
