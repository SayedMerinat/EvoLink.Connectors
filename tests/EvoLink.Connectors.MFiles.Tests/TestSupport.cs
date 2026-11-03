using System.Collections.Concurrent;
using EvoLink.Connectors.MFiles.Gateway;
using EvoLink.Logging.Classes;
using Ortolan.Core.Logging;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;

namespace EvoLink.Connectors.MFiles.Tests
{
    /// <summary>Identifiers of the test vault.</summary>
    public static class V
    {
        public const int DocumentClass = 0;
        public const int InvoiceClass = 5;
        public const int ContactClass = 20;
        public const int ContactType = 150;

        public const int NameOrTitle = 0;
        public const int Number = 1001;
        public const int Amount = 1002;
        public const int Quantity = 1003;
        public const int DocumentDate = 1004;
        public const int Active = 1005;
        public const int Country = 1006;
        public const int Categories = 1007;
        public const int Customer = 1008;
        public const int ExternalLogin = 1009;
        public const int Account = 1010;
        public const int Computed = 1011;

        public const int UsersList = 6;
        public const int CountryList = 101;
        public const int CategoryList = 102;
        public const int CustomerType = 103;

        public const int Admin = 1;
        public const int JDoe = 5;
        public const int ASmith = 7;

        public static FakeMFilesGateway CreateVault()
        {
            var vault = new FakeMFilesGateway();

            vault.Classes.AddRange(
            [
                new MFilesClassInfo(DocumentClass, "Document", 0, NameOrTitle),
                new MFilesClassInfo(InvoiceClass, "Facture", 0, NameOrTitle),
                new MFilesClassInfo(ContactClass, "Contact externe", ContactType, NameOrTitle),
            ]);
            vault.ClassAliases["CL.Facture"] = InvoiceClass;
            vault.ClassAliases["CL.Contact"] = ContactClass;

            vault.PropertyDefs.AddRange(
            [
                new MFilesPropertyDefInfo(NameOrTitle, "Nom ou titre", MFilesDataType.Text, null, false),
                new MFilesPropertyDefInfo(20, "Créé", MFilesDataType.Timestamp, null, false),
                new MFilesPropertyDefInfo(21, "Dernière modification", MFilesDataType.Timestamp, null, false),
                new MFilesPropertyDefInfo(23, "Dernière modification par", MFilesDataType.Lookup, UsersList, false),
                new MFilesPropertyDefInfo(25, "Créé par", MFilesDataType.Lookup, UsersList, false),
                new MFilesPropertyDefInfo(100, "Classe", MFilesDataType.Lookup, 1, false),
                new MFilesPropertyDefInfo(Number, "Numéro", MFilesDataType.Text, null, false, "{6F0C0E3A-1111-4222-8333-944455556666}"),
                new MFilesPropertyDefInfo(Amount, "Montant", MFilesDataType.Floating, null, false),
                new MFilesPropertyDefInfo(Quantity, "Quantité", MFilesDataType.Integer, null, false),
                new MFilesPropertyDefInfo(DocumentDate, "Date document", MFilesDataType.Date, null, false),
                new MFilesPropertyDefInfo(Active, "Actif", MFilesDataType.Boolean, null, false),
                new MFilesPropertyDefInfo(Country, "Pays", MFilesDataType.Lookup, CountryList, false),
                new MFilesPropertyDefInfo(Categories, "Catégories", MFilesDataType.MultiSelectLookup, CategoryList, false),
                new MFilesPropertyDefInfo(Customer, "Client", MFilesDataType.Lookup, CustomerType, false),
                new MFilesPropertyDefInfo(ExternalLogin, "Login externe", MFilesDataType.Text, null, false),
                new MFilesPropertyDefInfo(Account, "Compte", MFilesDataType.Lookup, UsersList, false),
                new MFilesPropertyDefInfo(Computed, "Calculé", MFilesDataType.Text, null, true),
            ]);
            vault.PropertyAliases["PD.Numero"] = Number;
            vault.PropertyAliases["PD.Pays"] = Country;

            vault.ValueLists.AddRange(
            [
                new MFilesValueListInfo(1, "Classe", "Classes", false),
                new MFilesValueListInfo(UsersList, "Utilisateur", "Utilisateurs", false),
                new MFilesValueListInfo(CountryList, "Pays", "Pays", false),
                new MFilesValueListInfo(CategoryList, "Catégorie", "Catégories", false),
                new MFilesValueListInfo(CustomerType, "Client", "Clients", true),
            ]);
            vault.ValueListAliases["VL.Pays"] = CountryList;

            vault.Items[CountryList] = [new(1, "Suisse", "CH"), new(2, "France", "FR"), new(3, "Allemagne", "DE")];
            vault.Items[CategoryList] = [new(1, "Contrat"), new(2, "Facture"), new(3, "Offre")];
            vault.Items[CustomerType] = [new(10, "ACME"), new(11, "Globex")];

            vault.Users.AddRange([new MFilesUserAccount(Admin, "admin"), new MFilesUserAccount(JDoe, @"DOMAIN\jdoe"), new MFilesUserAccount(ASmith, "asmith")]);

            return vault;
        }

        public static JobContext Context(SyncMode mode = SyncMode.Incremental, IOrtolanLogger? logger = null, params string[] keyFields)
            => new()
            {
                JobId = Guid.NewGuid().ToString("N"),
                ServiceId = "service",
                Mode = mode,
                Sync = new SyncOptions { KeyFields = keyFields.Length > 0 ? [.. keyFields] : ["Numéro"], DeleteMissing = true },
                Logger = logger ?? NullOrtolanLogger.Instance,
            };

        public static Entity Entity(params (string Name, object? Value)[] properties)
        {
            var entity = new Entity();
            foreach (var (name, value) in properties)
                entity[name] = value;
            return entity;
        }
    }

    /// <summary>Logger keeping the messages, to check the warnings and that no secret is written.</summary>
    public sealed class CapturingLogger : IOrtolanLogger
    {
        public ConcurrentQueue<(Severity Severity, string Message)> Messages { get; } = new();

        public void Log(Severity severity, string message, string? serviceId = null, string? jobId = null, string? category = null)
            => Messages.Enqueue((severity, message));

        public IEnumerable<string> Warnings
            => Messages.Where(m => m.Severity == Severity.Warning).Select(m => m.Message);
    }

    public class TestSynchroniser(FakeMFilesGateway fake) : MFilesSynchroniser
    {
        public FakeMFilesGateway Fake { get; } = fake;

        protected override IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
        {
            Fake.Settings = settings;
            return Fake;
        }
    }

    public class TestProvider(FakeMFilesGateway fake) : MFilesProvider
    {
        protected override IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
        {
            fake.Settings = settings;
            return fake;
        }
    }

    public class TestUserLookup(FakeMFilesGateway fake) : MFilesUserLookup
    {
        protected override IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
        {
            fake.Settings = settings;
            return fake;
        }
    }

    public class TestValueLookup(FakeMFilesGateway fake) : MFilesValueLookup
    {
        protected override IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
        {
            fake.Settings = settings;
            return fake;
        }
    }
}
