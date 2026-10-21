using System.Text.Json;
using ScanBack.Web.Models;

namespace ScanBack.Web.Services;

/// <summary>A reviewed allowlist. It never accepts account IDs, tokens or request hostnames.</summary>
public static class PublicMarketingCatalog
{
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "en", "el" });
    public static IReadOnlyList<string> Slugs { get; } = Array.AsReadOnly(new[] { "", "how-it-works", "qr-nfc-helmet-stickers", "motorcycle-emergency-guide" });

    public static PublicMarketingPage Page(string language, string slug, SiteLinks links)
    {
        if (!Languages.Contains(language) || !Slugs.Contains(slug)) throw new ArgumentOutOfRangeException(nameof(slug));
        var greek = language == "el";
        var copy = Copy(slug, greek);
        var path = "/" + language + (slug.Length == 0 ? "" : "/" + slug);
        var canonical = links.Absolute(path);
        var alternates = Languages.Select(value => new PublicSeoAlternate(value,
            links.Absolute("/" + value + (slug.Length == 0 ? "" : "/" + slug)))).ToArray();
        var organizationId = links.Absolute("/#organization");
        var graph = new List<object>
        {
            new Dictionary<string, object>
            {
                ["@type"] = "Organization", ["@id"] = organizationId,
                ["name"] = "ScanBack", ["url"] = links.Absolute("/"),
                ["logo"] = links.Absolute("/favicon.svg")
            },
            new Dictionary<string, object>
            {
                ["@type"] = "WebSite", ["@id"] = links.Absolute("/#website"),
                ["name"] = "ScanBack", ["url"] = links.Absolute("/"),
                ["inLanguage"] = new[] { "en", "el" },
                ["publisher"] = new Dictionary<string, object> { ["@id"] = organizationId }
            }
        };
        var breadcrumbs = new List<object>
        {
            new Dictionary<string, object> { ["@type"] = "ListItem", ["position"] = 1, ["name"] = greek ? "Αρχική" : "Home", ["item"] = links.Absolute("/" + language) }
        };
        if (slug.Length != 0) breadcrumbs.Add(new Dictionary<string, object> { ["@type"] = "ListItem", ["position"] = 2, ["name"] = copy.Heading, ["item"] = canonical });
        graph.Add(new Dictionary<string, object> { ["@type"] = "BreadcrumbList", ["itemListElement"] = breadcrumbs });
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@graph"] = graph
        });
        return new(new(language, copy.Title, copy.Description, canonical, links.Absolute("/images/scanback-social.png"),
            alternates, json, slug), copy.Heading, copy.Introduction, copy.Sections);
    }

    private sealed record PageCopy(string Title, string Description, string Heading, string Introduction, IReadOnlyList<MarketingSection> Sections);

    private static PageCopy Copy(string slug, bool greek) => (slug, greek) switch
    {
        ("", false) => new("QR & NFC Motorcycle Emergency Profiles | ScanBack",
            "Prepare a QR or NFC helmet sticker with ScanBack. Choose the emergency information and trusted contacts a helper can access. Available in English and Greek.",
            "Your motorcycle emergency profile", "QR and NFC helmet stickers connect a helper to the information you choose to share.", []),
        ("", true) => new("Προφίλ έκτακτης ανάγκης με QR & NFC για μοτοσικλετιστές | ScanBack",
            "Ετοιμάστε αυτοκόλλητο QR ή ετικέτα NFC για το κράνος σας. Επιλέξτε ποιες πληροφορίες και επαφές έκτακτης ανάγκης μοιράζεστε. Στα ελληνικά και αγγλικά.",
            "Το προφίλ έκτακτης ανάγκης του μοτοσικλετιστή", "Αυτοκόλλητα QR και ετικέτες NFC συνδέουν όποιον βοηθά με τις πληροφορίες που επιλέγετε να μοιραστείτε.", []),
        ("how-it-works", false) => new("How ScanBack Works: QR Emergency Profiles for Riders",
            "Learn how to create a rider emergency profile, choose shared details, verify trusted contacts and connect a QR helmet sticker or NFC tag to ScanBack.",
            "How ScanBack works", "Prepare before a ride so someone who scans your helmet sticker can find the information you have chosen to share.",
            [
                new("1. Set up your rider profile", ["Create an account and confirm your email address. In your dashboard, add your name and the emergency information you want to keep ready. Health information is optional and requires your consent.", "Saving a detail does not automatically make it visible on your emergency page. Review the visibility choices for each field, then preview the page a person scanning your sticker can open."]),
                new("2. Ask trusted contacts to take part", ["Add people who can help when they receive a possible accident report. ScanBack emails an invitation so each person can choose whether to receive notifications and whether their phone number may appear on your emergency page.", "A contact's approval matters: entering someone else's email or phone number does not give permission to publish their details. Keep contact details current and discuss what you would want them to do."]),
                new("3. Connect a QR sticker or NFC tag", ["Create a sticker in your dashboard and download the QR image. The code contains a link to your emergency page, rather than your medical details. The same link can be written to a compatible NFC tag using a separate NFC writing tool.", "Test the printed QR code and any NFC tag with a phone before using them. A readable sticker, a compatible device and an internet connection are needed to open the page. No ScanBack app is required."]),
                new("4. Understand what happens after a scan", ["A helper can view the details you have made available and use the links to call emergency services. If they submit a possible accident report, ScanBack can queue email notifications for verified contacts who have agreed to receive them.", "Scanning a code or submitting a report does not call 112, 166 or an ambulance. Email delivery and a contact's response are not guaranteed. A helper should call emergency services directly when someone needs urgent help."]),
                new("Keep the information ready", ["Review your profile after a change in health information or contact details. If a sticker is lost or copied, deactivate it and create a replacement. You can change field visibility, withdraw health consent, export your data or delete your account from your workspace."])
            ]),
        ("how-it-works", true) => new("Πώς λειτουργεί το ScanBack: QR προφίλ έκτακτης ανάγκης",
            "Δείτε πώς δημιουργείτε προφίλ μοτοσικλετιστή, επιλέγετε ορατά στοιχεία, επιβεβαιώνετε επαφές και συνδέετε αυτοκόλλητο QR ή ετικέτα NFC με το ScanBack.",
            "Πώς λειτουργεί το ScanBack", "Προετοιμαστείτε πριν από τη διαδρομή, ώστε όποιος σαρώσει το αυτοκόλλητο στο κράνος σας να βρει τις πληροφορίες που έχετε επιλέξει να μοιραστείτε.",
            [
                new("1. Δημιουργήστε το προφίλ σας", ["Δημιουργήστε λογαριασμό και επιβεβαιώστε το email σας. Στον πίνακα ελέγχου προσθέστε το όνομά σας και τις πληροφορίες έκτακτης ανάγκης που θέλετε να έχετε διαθέσιμες. Τα δεδομένα υγείας είναι προαιρετικά και απαιτούν τη συγκατάθεσή σας.", "Η αποθήκευση ενός στοιχείου δεν το καθιστά αυτόματα ορατό στη σελίδα έκτακτης ανάγκης. Ελέγξτε τις επιλογές ορατότητας για κάθε πεδίο και δείτε την προεπισκόπηση της σελίδας που ανοίγει η σάρωση."]),
                new("2. Προσκαλέστε τις επαφές σας", ["Προσθέστε ανθρώπους που μπορούν να βοηθήσουν όταν λάβουν αναφορά πιθανού ατυχήματος. Το ScanBack στέλνει πρόσκληση με email, ώστε κάθε άτομο να επιλέξει αν δέχεται ειδοποιήσεις και αν επιτρέπει την εμφάνιση του τηλεφώνου του στη σελίδα έκτακτης ανάγκης.", "Η έγκριση της επαφής είναι απαραίτητη: η καταχώριση του email ή του τηλεφώνου κάποιου δεν αποτελεί άδεια δημοσίευσης των στοιχείων του. Διατηρείτε τις επαφές ενημερωμένες και συζητήστε μαζί τους τι θα θέλατε να κάνουν."]),
                new("3. Συνδέστε αυτοκόλλητο QR ή ετικέτα NFC", ["Δημιουργήστε αυτοκόλλητο στον πίνακα ελέγχου και κατεβάστε την εικόνα QR. Ο κωδικός περιέχει σύνδεσμο προς τη σελίδα έκτακτης ανάγκης, όχι τα ιατρικά σας στοιχεία. Ο ίδιος σύνδεσμος μπορεί να γραφτεί σε συμβατή ετικέτα NFC με ξεχωριστό εργαλείο εγγραφής NFC.", "Δοκιμάστε το εκτυπωμένο QR και την ετικέτα NFC με κινητό πριν τα χρησιμοποιήσετε. Για το άνοιγμα της σελίδας χρειάζονται ευανάγνωστο αυτοκόλλητο, συμβατή συσκευή και σύνδεση στο διαδίκτυο. Δεν απαιτείται εφαρμογή ScanBack."]),
                new("4. Τι συμβαίνει μετά τη σάρωση", ["Όποιος βοηθά μπορεί να δει όσα στοιχεία έχετε επιτρέψει και να χρησιμοποιήσει τους συνδέσμους κλήσης των υπηρεσιών έκτακτης ανάγκης. Αν υποβάλει αναφορά πιθανού ατυχήματος, το ScanBack μπορεί να βάλει σε ουρά email προς επιβεβαιωμένες επαφές που δέχονται ειδοποιήσεις.", "Η σάρωση ή η υποβολή αναφοράς δεν καλεί το 112, το 166 ή ασθενοφόρο. Η παράδοση email και η ανταπόκριση μιας επαφής δεν είναι εγγυημένες. Σε επείγουσα ανάγκη, όποιος βοηθά πρέπει να καλέσει απευθείας τις αρμόδιες υπηρεσίες."]),
                new("Διατηρείτε τα στοιχεία ενημερωμένα", ["Ελέγχετε το προφίλ όταν αλλάζουν τα στοιχεία υγείας ή οι επαφές σας. Αν χαθεί ή αντιγραφεί ένα αυτοκόλλητο, απενεργοποιήστε το και δημιουργήστε άλλο. Από τον χώρο εργασίας μπορείτε να αλλάξετε την ορατότητα, να ανακαλέσετε τη συγκατάθεση υγείας, να εξαγάγετε δεδομένα ή να διαγράψετε τον λογαριασμό σας."])
            ]),
        ("qr-nfc-helmet-stickers", false) => new("QR & NFC Helmet Stickers for Motorcycle Riders | ScanBack",
            "Compare QR helmet stickers and NFC tags for a motorcycle emergency profile. Learn what the link shares, how to test it and how to replace a lost sticker.",
            "QR and NFC helmet stickers for motorcycle riders", "Give a helper a clear way to open your chosen emergency information, without printing health details or contact numbers on your helmet.",
            [
                new("One emergency link, two ways to open it", ["A QR code is read by a phone camera or QR reader. An NFC tag holds the same web link and can open it when tapped by a compatible phone with NFC enabled. ScanBack provides the QR image and emergency link; NFC hardware and writing tools are separate.", "The page opens in a browser. Both methods depend on the sticker or tag being readable, the phone being compatible and an internet connection being available. Keep a readable QR option even when using NFC, and test both with the equipment you use."]),
                new("What is stored on the sticker?", ["The QR code and NFC tag contain a link with an access token. Medical fields and phone numbers are retrieved from the page according to the rider's visibility choices and each contact's permission; they are not written into the QR image itself.", "Anyone who can read or copy the link can try to open the emergency page while that sticker is active. Treat it as a link intended for a person helping you, and share only details you are comfortable making accessible that way. Search engine exclusion is an extra precaution, not access control."]),
                new("Prepare and test your sticker", ["Use a clear print with good contrast and enough space around the QR code. Check that the destination is your intended emergency page, and review the visible fields on a separate phone before riding."], ["Follow the helmet manufacturer's guidance for adhesives, materials and placement. Do not drill, cut or alter protective equipment.", "Choose a visible location that does not obstruct your view, required markings, vents or moving parts.", "Test the QR code after printing and periodically check for fading, dirt or damage. NFC performance can change with the tag and surrounding materials.", "Keep your emergency contacts informed and review the page whenever their details or your sharing choices change."]),
                new("Replace a lost or copied link", ["Deactivate the affected sticker in your dashboard, then create and test a replacement. An old print or copied NFC link will no longer open an active emergency page after deactivation. Updating profile details changes what an active sticker shows; it does not require printing the same active code again."]),
                new("A preparation tool, with practical limits", ["A sticker is not a location tracker or an automatic crash detector. It cannot ensure someone will find it, scan it or provide help. ScanBack does not dispatch emergency services. In Greece, call 112 for an emergency or 166 for EKAB ambulance assistance when needed."])
            ]),
        ("qr-nfc-helmet-stickers", true) => new("Αυτοκόλλητα QR & NFC για κράνη μοτοσικλετιστών | ScanBack",
            "Συγκρίνετε αυτοκόλλητα QR και ετικέτες NFC για προφίλ έκτακτης ανάγκης. Μάθετε τι μοιράζεται ο σύνδεσμος, πώς τον δοκιμάζετε και πώς τον αντικαθιστάτε.",
            "Αυτοκόλλητα QR και ετικέτες NFC για κράνη", "Δώστε σε όποιον βοηθά έναν σαφή τρόπο πρόσβασης στις επιλεγμένες πληροφορίες έκτακτης ανάγκης, χωρίς εκτύπωση στοιχείων υγείας ή τηλεφώνων στο κράνος.",
            [
                new("Ένας σύνδεσμος, δύο τρόποι πρόσβασης", ["Το QR διαβάζεται από κάμερα κινητού ή εφαρμογή ανάγνωσης QR. Η ετικέτα NFC αποθηκεύει τον ίδιο σύνδεσμο και μπορεί να τον ανοίξει όταν την πλησιάσετε σε συμβατό κινητό με ενεργό NFC. Το ScanBack παρέχει την εικόνα QR και τον σύνδεσμο· η ετικέτα και το εργαλείο εγγραφής NFC παρέχονται ξεχωριστά.", "Η σελίδα ανοίγει στον περιηγητή. Και οι δύο μέθοδοι απαιτούν ευανάγνωστο κωδικό ή ετικέτα, συμβατό κινητό και σύνδεση στο διαδίκτυο. Διατηρείτε ευανάγνωστο QR ακόμη και αν χρησιμοποιείτε NFC και δοκιμάστε και τους δύο τρόπους με τον εξοπλισμό σας."]),
                new("Τι αποθηκεύεται στο αυτοκόλλητο;", ["Το QR και η ετικέτα NFC περιέχουν σύνδεσμο με διακριτικό πρόσβασης. Τα ιατρικά πεδία και τα τηλέφωνα εμφανίζονται από τη σελίδα σύμφωνα με τις επιλογές του μοτοσικλετιστή και την άδεια κάθε επαφής· δεν γράφονται μέσα στην εικόνα QR.", "Όποιος διαβάσει ή αντιγράψει τον σύνδεσμο μπορεί να προσπαθήσει να ανοίξει τη σελίδα όσο το αυτοκόλλητο είναι ενεργό. Θεωρήστε τον σύνδεσμο διαθέσιμο σε όποιον σας βοηθά και επιλέξτε ανάλογα τα στοιχεία που μοιράζεστε. Ο αποκλεισμός από μηχανές αναζήτησης είναι πρόσθετο μέτρο, όχι έλεγχος πρόσβασης."]),
                new("Προετοιμασία και δοκιμή", ["Εκτυπώστε καθαρά, με καλή αντίθεση και αρκετό κενό γύρω από τον κωδικό QR. Ελέγξτε ότι ανοίγει τη σωστή σελίδα και δοκιμάστε τα ορατά πεδία από άλλο κινητό πριν από τη διαδρομή."], ["Ακολουθείτε τις οδηγίες του κατασκευαστή του κράνους για κόλλες, υλικά και τοποθέτηση. Μην τρυπάτε, κόβετε ή τροποποιείτε προστατευτικό εξοπλισμό.", "Επιλέξτε ορατό σημείο που δεν εμποδίζει την όραση, τις απαιτούμενες σημάνσεις, τους αεραγωγούς ή τα κινούμενα μέρη.", "Δοκιμάστε το QR μετά την εκτύπωση και ελέγχετε για ξεθώριασμα, βρομιά ή φθορές. Η λειτουργία NFC επηρεάζεται από την ετικέτα και τα υλικά γύρω της.", "Ενημερώνετε τις επαφές σας και ελέγχετε τη σελίδα όταν αλλάζουν τα στοιχεία ή οι επιλογές κοινοποίησης."]),
                new("Αν χαθεί ή αντιγραφεί ο σύνδεσμος", ["Απενεργοποιήστε το αντίστοιχο αυτοκόλλητο από τον πίνακα ελέγχου και δημιουργήστε άλλο. Μετά την απενεργοποίηση, η παλιά εκτύπωση ή ο αντιγραμμένος σύνδεσμος NFC δεν ανοίγει ενεργή σελίδα έκτακτης ανάγκης. Η ενημέρωση του προφίλ αλλάζει όσα εμφανίζει ένα ενεργό αυτοκόλλητο, χωρίς να χρειάζεται νέα εκτύπωση του ίδιου ενεργού κωδικού."]),
                new("Εργαλείο προετοιμασίας με πρακτικούς περιορισμούς", ["Το αυτοκόλλητο δεν είναι συσκευή εντοπισμού ή αυτόματης ανίχνευσης ατυχήματος. Δεν εγγυάται ότι κάποιος θα το βρει, θα το σαρώσει ή θα προσφέρει βοήθεια. Το ScanBack δεν αποστέλλει υπηρεσίες έκτακτης ανάγκης. Στην Ελλάδα, καλέστε 112 σε επείγον περιστατικό ή 166 για βοήθεια ασθενοφόρου από το ΕΚΑΒ."])
            ]),
        ("motorcycle-emergency-guide", false) => new("Motorcycle Emergency Preparation in Greece: 112 & 166 | ScanBack",
            "Prepare emergency contacts and rider information before a motorcycle trip in Greece. Know when to call 112 or 166 and what to tell the emergency dispatcher.",
            "Motorcycle emergency preparation in Greece", "A practical communication checklist for riders and people who may help. In an emergency, call the emergency services directly and follow the dispatcher's instructions.",
            [
                new("Know the numbers before you ride", ["112 is the European emergency number, available in Greece for urgent help. 166 connects callers to EKAB, Greece's emergency ambulance service. If someone needs urgent help, call immediately; do not delay a call to scan a sticker, create an account or complete a report.", "ScanBack provides telephone links that let your device start a call. Opening an emergency page or submitting a possible accident report does not place that call and does not alert an emergency control centre automatically."]),
                new("Give the dispatcher useful facts", ["Describe what you can observe and answer the dispatcher's questions. If you do not know something, say so. Follow their instructions and let them decide what assistance is needed."], ["Where: road name, direction of travel, nearby junction, kilometre marker or a clear landmark. Give coordinates if you have them and the dispatcher asks.", "What happened: say that a motorcycle is involved and describe the visible situation without guessing.", "How many: explain how many people appear to be involved and any immediate hazards you can see.", "Contact: give the number you are calling from and stay available for further instructions."]),
                new("Prepare your information and contacts", ["Before a journey, review your trusted contacts and discuss what they should do if they hear about a possible accident. Include international dialling codes when travelling. Keep an alternative way to find essential contacts if your phone is unavailable or the network is down.", "If you use a ScanBack emergency page, review its visible fields and test the QR or NFC link. Share only information you have chosen to make accessible. Information on a rider's page is supplied by that rider and is not a verified medical record."]),
                new("Share a location deliberately", ["If you submit a ScanBack report, location sharing requires your permission. Check the map or coordinates and add a useful description when possible. Device location can be inaccurate, especially near tunnels, buildings or remote roads. Tell the emergency dispatcher your location directly; a location added to a ScanBack report is not sent to 112 or 166 by the application."]),
                new("Use official instructions for emergency care", ["This guide concerns preparation and communication, not medical assessment or first aid training. Use current guidance from the Greek emergency authorities and follow the dispatcher. A QR profile supports communication; it does not replace trained responders or emergency services."])
            ]),
        ("motorcycle-emergency-guide", true) => new("Προετοιμασία για ατύχημα με μοτοσικλέτα: 112 & 166 | ScanBack",
            "Οργανώστε επαφές και πληροφορίες πριν από μια διαδρομή στην Ελλάδα. Μάθετε τους αριθμούς 112 και 166 και ποιες πληροφορίες χρειάζεται ο τηλεφωνητής.",
            "Προετοιμασία για έκτακτη ανάγκη με μοτοσικλέτα στην Ελλάδα", "Πρακτικός οδηγός επικοινωνίας για μοτοσικλετιστές και όσους μπορεί να βοηθήσουν. Σε επείγον περιστατικό καλέστε απευθείας τις αρμόδιες υπηρεσίες και ακολουθήστε τις οδηγίες τους.",
            [
                new("Γνωρίστε τους αριθμούς πριν ξεκινήσετε", ["Το 112 είναι ο ευρωπαϊκός αριθμός έκτακτης ανάγκης και λειτουργεί στην Ελλάδα για επείγουσα βοήθεια. Το 166 συνδέει με το ΕΚΑΒ, την υπηρεσία επείγουσας προνοσοκομειακής φροντίδας. Αν κάποιος χρειάζεται άμεση βοήθεια, καλέστε χωρίς καθυστέρηση· μην περιμένετε να σαρώσετε αυτοκόλλητο, να δημιουργήσετε λογαριασμό ή να υποβάλετε αναφορά.", "Το ScanBack παρέχει συνδέσμους τηλεφώνου ώστε η συσκευή σας να ξεκινήσει κλήση. Το άνοιγμα της σελίδας ή η υποβολή αναφοράς πιθανού ατυχήματος δεν πραγματοποιεί κλήση και δεν ειδοποιεί αυτόματα κέντρο έκτακτης ανάγκης."]),
                new("Δώστε χρήσιμα στοιχεία στον τηλεφωνητή", ["Περιγράψτε όσα παρατηρείτε και απαντήστε στις ερωτήσεις. Αν δεν γνωρίζετε κάτι, πείτε το. Ακολουθήστε τις οδηγίες και αφήστε τις αρμόδιες υπηρεσίες να κρίνουν ποια βοήθεια χρειάζεται."], ["Πού: ονομασία δρόμου, κατεύθυνση, κοντινός κόμβος, χιλιομετρική ένδειξη ή σαφές σημείο αναφοράς. Δώστε συντεταγμένες αν τις έχετε και σας ζητηθούν.", "Τι συνέβη: αναφέρετε ότι εμπλέκεται μοτοσικλέτα και περιγράψτε την ορατή κατάσταση χωρίς υποθέσεις.", "Πόσοι: αναφέρετε πόσα άτομα φαίνεται να εμπλέκονται και τυχόν άμεσους κινδύνους που βλέπετε.", "Επικοινωνία: δώστε τον αριθμό από τον οποίο καλείτε και παραμείνετε διαθέσιμοι για οδηγίες."]),
                new("Οργανώστε στοιχεία και επαφές", ["Πριν από τη διαδρομή, ελέγξτε τις επαφές σας και συζητήστε τι θα πρέπει να κάνουν αν ενημερωθούν για πιθανό ατύχημα. Χρησιμοποιήστε διεθνείς κωδικούς τηλεφώνου όταν ταξιδεύετε. Προβλέψτε εναλλακτικό τρόπο εύρεσης βασικών επαφών αν το κινητό δεν είναι διαθέσιμο ή δεν υπάρχει δίκτυο.", "Αν χρησιμοποιείτε σελίδα ScanBack, ελέγξτε τα ορατά πεδία και δοκιμάστε τον σύνδεσμο QR ή NFC. Μοιραστείτε μόνο όσα έχετε επιλέξει να είναι προσβάσιμα. Οι πληροφορίες εισάγονται από τον μοτοσικλετιστή και δεν αποτελούν επαληθευμένο ιατρικό αρχείο."]),
                new("Κοινοποιήστε την τοποθεσία συνειδητά", ["Η κοινοποίηση τοποθεσίας σε αναφορά ScanBack απαιτεί την άδειά σας. Ελέγξτε τον χάρτη ή τις συντεταγμένες και, αν μπορείτε, προσθέστε χρήσιμη περιγραφή. Ο εντοπισμός της συσκευής μπορεί να είναι ανακριβής, ιδίως κοντά σε σήραγγες, κτίρια ή απομονωμένους δρόμους. Δώστε την τοποθεσία απευθείας στον τηλεφωνητή: η εφαρμογή δεν στέλνει στο 112 ή στο 166 τη θέση που προσθέτετε στην αναφορά."]),
                new("Ακολουθήστε επίσημες οδηγίες για τη φροντίδα", ["Αυτός ο οδηγός αφορά προετοιμασία και επικοινωνία, όχι ιατρική εκτίμηση ή εκπαίδευση πρώτων βοηθειών. Συμβουλεύεστε τις ισχύουσες οδηγίες των ελληνικών αρμόδιων αρχών και ακολουθείτε τον τηλεφωνητή. Το προφίλ QR υποστηρίζει την επικοινωνία· δεν αντικαθιστά διασώστες ή υπηρεσίες έκτακτης ανάγκης."])
            ]),
        _ => throw new ArgumentOutOfRangeException(nameof(slug))
    };
}
