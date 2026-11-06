using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.MFiles.Tests
{
    public class SynchroniserTests
    {
        private readonly FakeMFilesGateway _vault = V.CreateVault();

        private TestSynchroniser Synchroniser(Action<MFilesSynchroniser>? configure = null)
        {
            var synchroniser = new TestSynchroniser(_vault) { Vault = "Test", Class = "Facture" };
            configure?.Invoke(synchroniser);
            return synchroniser;
        }

        private static Entity Invoice(string number, object? amount = null, object? country = null)
            => V.Entity(("Numéro", number), ("Nom ou titre", $"Facture {number}"), ("Montant", amount), ("Pays", country));

        [Fact]
        public async Task Create_ConvertsTheValuesAndResolvesTheLookups()
        {
            var entity = V.Entity(("Numéro", "F-1"), ("Nom ou titre", "Facture 1"), ("Montant", "1'234,50"), ("Quantité", "3"),
                ("Date document", "31.12.2024"), ("Actif", "oui"), ("Pays", "France"), ("Catégories", "Contrat;Offre"));
            var context = V.Context();

            var output = await Synchroniser().ActAsync([entity], context);

            Assert.Equal(1, context.Report.Created);
            Assert.Equal(SyncResult.Created, output[0].SyncResult);

            var created = Assert.Single(_vault.Alive(V.InvoiceClass));
            Assert.Equal("F-1", created.Value(V.Number));
            Assert.Equal(1234.5, created.Value(V.Amount));
            Assert.Equal(3L, created.Value(V.Quantity));
            Assert.Equal(new DateTime(2024, 12, 31), created.Value(V.DocumentDate));
            Assert.Equal(true, created.Value(V.Active));
            Assert.Equal(2, Assert.IsType<MFilesLookup>(created.Value(V.Country)).Id);
            Assert.Equal([1, 3], Assert.IsAssignableFrom<IEnumerable<MFilesLookup>>(created.Value(V.Categories)).Select(l => l.Id));

            // The created object is referenced by the entity.
            Assert.Equal(created.Id.ToString(), output[0].Metadata[MFilesKeys.MetadataObjectId]);
            Assert.Equal(0, _vault.Creates.Single().ObjectType);
        }

        [Fact]
        public async Task Update_WritesOnlyTheChangedProperties()
        {
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.NameOrTitle, "Facture F-1"), (V.Amount, 10.0), (V.Country, new MFilesLookup(1, "Suisse")));
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-2"), (V.NameOrTitle, "Facture F-2"), (V.Amount, 20.0), (V.Country, new MFilesLookup(2, "France")));
            var context = V.Context();

            await Synchroniser().ActAsync([Invoice("F-1", 10, "Allemagne"), Invoice("F-2", 20, "France")], context);

            Assert.Equal(1, context.Report.Updated);
            Assert.Equal(1, context.Report.Unchanged);

            var update = Assert.Single(_vault.Updates);
            var value = Assert.Single(update.Values);
            Assert.Equal(V.Country, value.PropertyDefId);
            Assert.Equal(3, Assert.IsType<MFilesLookup>(value.Value).Id);
            Assert.Equal(2, _vault.Objects[0].Version);
        }

        [Fact]
        public async Task Update_SameValueInAnotherRepresentation_DoesNotWrite()
        {
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.NameOrTitle, "Facture F-1"), (V.Amount, 10.5), (V.Country, new MFilesLookup(1, "Suisse")));

            // "10.50" differs textually from 10.5 and the country is given by its ID: the planner sees a change, M-Files does not.
            await Synchroniser().ActAsync([Invoice("F-1", "10.50", "1")], V.Context());

            Assert.Empty(_vault.Updates);
            Assert.Equal(0, _vault.Calls("Update"));
        }

        [Fact]
        public async Task Update_WithSyncFields_WritesOnlyThoseFields()
        {
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.NameOrTitle, "Ancien titre"), (V.Amount, 10.0));
            var synchroniser = Synchroniser(s => s.SyncFields = ["Montant"]);

            await synchroniser.ActAsync([V.Entity(("Numéro", "F-1"), ("Nom ou titre", "Nouveau titre"), ("Montant", 11))], V.Context());

            var update = Assert.Single(_vault.Updates);
            Assert.Equal([V.Amount], update.Values.Select(v => v.PropertyDefId));
            Assert.Equal("Ancien titre", _vault.Objects[0].Value(V.NameOrTitle));
        }

        [Fact]
        public async Task Delete_MarksTheObjectAsDeletedByDefault()
        {
            var obj = _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.NameOrTitle, "Facture F-1"));
            var deletion = V.Entity(("Numéro", "F-1"));
            deletion.SyncAction = SyncAction.Delete;
            var context = V.Context();

            await Synchroniser().ActAsync([deletion], context);

            Assert.Equal(1, context.Report.Deleted);
            Assert.True(obj.Deleted);
            Assert.Contains(obj, _vault.Objects);
        }

        [Fact]
        public async Task Delete_DestroysTheObjectWhenConfigured()
        {
            var obj = _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.NameOrTitle, "Facture F-1"));
            var deletion = V.Entity(("Numéro", "F-1"));
            deletion.SyncAction = SyncAction.Delete;

            await Synchroniser(s => s.DestroyOnDelete = true).ActAsync([deletion], V.Context());

            Assert.DoesNotContain(obj, _vault.Objects);
        }

        [Fact]
        public async Task Mirror_DeletesTheObjectsMissingFromTheSource_WithinTheConditions()
        {
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.Country, new MFilesLookup(1)));
            var orphan = _vault.AddObject(V.InvoiceClass, (V.Number, "F-9"), (V.Country, new MFilesLookup(1)));
            var otherScope = _vault.AddObject(V.InvoiceClass, (V.Number, "F-8"), (V.Country, new MFilesLookup(2)));
            var context = V.Context(SyncMode.Mirror);

            await Synchroniser(s => { s.Conditions = new() { ["Pays"] = "Suisse" }; s.Fields = new() { ["Numéro"] = "Numéro" }; s.OnlyMappedFields = true; })
                .ActAsync([V.Entity(("Numéro", "F-1"))], context);

            Assert.Equal(1, context.Report.Deleted);
            Assert.True(orphan.Deleted);
            Assert.False(otherScope.Deleted);
        }

        [Fact]
        public async Task Lookups_AreResolvedByNameDisplayIdOrId()
        {
            var entities = new List<Entity>
            {
                Invoice("F-1", 1, "france"),
                Invoice("F-2", 1, "DE"),
                Invoice("F-3", 1, 1),
                V.Entity(("Numéro", "F-4"), ("Catégories", new List<object?> { "Offre", 1L, "facture" })),
            };

            await Synchroniser().ActAsync(entities, V.Context());

            var objects = _vault.Alive(V.InvoiceClass).ToDictionary(o => (string)o.Value(V.Number)!);
            Assert.Equal(2, ((MFilesLookup)objects["F-1"].Value(V.Country)!).Id);
            Assert.Equal(3, ((MFilesLookup)objects["F-2"].Value(V.Country)!).Id);
            Assert.Equal(1, ((MFilesLookup)objects["F-3"].Value(V.Country)!).Id);
            Assert.Equal([3, 1, 2], ((IEnumerable<MFilesLookup>)objects["F-4"].Value(V.Categories)!).Select(l => l.Id));
        }

        [Fact]
        public async Task MissingLookupItem_FailsTheEntityOnly()
        {
            var context = V.Context();

            var output = await Synchroniser().ActAsync([Invoice("F-1", 1, "Italie"), Invoice("F-2", 1, "Suisse")], context);

            Assert.Equal(SyncResult.Failed, output.Single(e => e.GetString("Numéro") == "F-1").SyncResult);
            Assert.Contains("Italie", output.Single(e => e.GetString("Numéro") == "F-1").Error);
            Assert.Equal(1, context.Report.Created);
            Assert.Equal(0, _vault.Calls("AddValueListItem"));
        }

        [Fact]
        public async Task MissingLookupItem_IsCreatedWhenAllowed_ButNeverForObjectTypes()
        {
            var output = await Synchroniser(s => s.CreateMissingValueListItems = true)
                .ActAsync([Invoice("F-1", 1, "Italie"), V.Entity(("Numéro", "F-2"), ("Client", "Initech"))], V.Context());

            Assert.Equal(SyncResult.Created, output[0].SyncResult);
            Assert.Contains(_vault.Items[V.CountryList], i => i.Name == "Italie");
            Assert.Equal(SyncResult.Failed, output[1].SyncResult);
            Assert.DoesNotContain(_vault.Items[V.CustomerType], i => i.Name == "Initech");
        }

        [Theory]
        [InlineData("Facture")]
        [InlineData("facture")]
        [InlineData("CL.Facture")]
        [InlineData("5")]
        public async Task Class_IsResolvedByNameAliasOrId(string identifier)
        {
            await Synchroniser(s => s.Class = identifier).ActAsync([Invoice("F-1")], V.Context());

            Assert.Single(_vault.Alive(V.InvoiceClass));
        }

        [Fact]
        public async Task Fields_AreResolvedByNameAliasIdOrGuid()
        {
            var synchroniser = Synchroniser(s => s.Fields = new()
            {
                ["number"] = "PD.Numero",
                ["amount"] = "1002",
                ["country"] = "pays",
                ["title"] = "Nom ou titre",
            });

            var context = V.Context(keyFields: "number");
            await synchroniser.ActAsync([V.Entity(("number", "F-1"), ("amount", 5), ("country", "Suisse"), ("title", "T"))], context);

            var created = Assert.Single(_vault.Alive(V.InvoiceClass));
            Assert.Equal("F-1", created.Value(V.Number));
            Assert.Equal(5.0, created.Value(V.Amount));

            // Second run: the target is read back under the entity names, nothing changes.
            var second = V.Context(keyFields: "number");
            await synchroniser.ActAsync([V.Entity(("number", "F-1"), ("amount", 5), ("country", "Suisse"), ("title", "T"))], second);
            Assert.Equal(1, second.Report.Unchanged);

            var session = new MFilesSession(_vault);
            Assert.Equal(V.Number, session.FindProperty("{6f0c0e3a-1111-4222-8333-944455556666}")?.Id);
        }

        [Fact]
        public async Task UnknownProperty_FailsTheEntity_UnlessIgnored()
        {
            var entity = () => V.Entity(("Numéro", "F-1"), ("Inconnue", "x"));

            var strict = await Synchroniser().ActAsync([entity()], V.Context());
            Assert.Equal(SyncResult.Failed, strict[0].SyncResult);
            Assert.Contains("Inconnue", strict[0].Error);

            var tolerant = await Synchroniser(s => s.IgnoreUnknownProperties = true).ActAsync([entity()], V.Context());
            Assert.Equal(SyncResult.Created, tolerant[0].SyncResult);
        }

        [Fact]
        public async Task ManagedProperties_AreNeverWritten()
        {
            var entity = V.Entity(("Numéro", "F-1"), ("Calculé", "x"), ("Créé", "2024-01-01"), ("Classe", "Document"));

            await Synchroniser().ActAsync([entity], V.Context());

            var request = Assert.Single(_vault.Creates);
            Assert.Equal([V.Number], request.Values.Select(v => v.PropertyDefId));
            Assert.Equal(V.InvoiceClass, request.ClassId);
        }

        [Fact]
        public async Task Create_AttachesTheFile_AndNamesTheDocumentAfterIt()
        {
            var path = Path.Combine(Path.GetTempPath(), $"mfiles-test-{Guid.NewGuid():N}.pdf");
            await File.WriteAllTextAsync(path, "pdf");

            try
            {
                var withoutName = V.Entity(("Numéro", "F-1"), (MFilesKeys.FilePath, path), ("FILE-Extension", ".pdf"));
                var withExtension = V.Entity(("Numéro", "F-2"), ("Nom ou titre", "Contrat.PDF"), (MFilesKeys.FilePath, path));

                var output = await Synchroniser().ActAsync([withoutName, withExtension], V.Context());

                Assert.All(output, e => Assert.Equal(SyncResult.Created, e.SyncResult));
                var requests = _vault.Creates.OrderBy(r => r.Values.First(v => v.PropertyDefId == V.Number).Value).ToList();

                Assert.Equal(path, requests[0].File?.Path);
                Assert.Equal("pdf", requests[0].File?.Extension);
                Assert.Equal(Path.GetFileNameWithoutExtension(path), requests[0].Values.Single(v => v.PropertyDefId == V.NameOrTitle).Value);
                Assert.DoesNotContain(requests[0].Values, v => v.Value as string == ".pdf");

                Assert.Equal("Contrat", requests[1].Values.Single(v => v.PropertyDefId == V.NameOrTitle).Value);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task Create_MissingFile_FailsTheEntity_NotTheTarget()
        {
            var entity = V.Entity(("Numéro", "F-1"), (MFilesKeys.FilePath, @"C:\nulle\part\absent.pdf"));

            var output = await Synchroniser().ActAsync([entity], V.Context());

            Assert.Equal(SyncResult.Failed, output[0].SyncResult);
            Assert.Contains("absent.pdf", output[0].Error);
        }

        [Fact]
        public async Task Create_MissingFile_WithoutRequireFile_CreatesTheObjectWithoutFile()
        {
            var logger = new CapturingLogger();
            var missing = V.Entity(("Numéro", "F-1"), (MFilesKeys.FilePath, @"C:\nulle\part\absent.pdf"));
            var noPath = V.Entity(("Numéro", "F-2"));

            var output = await Synchroniser(s => s.RequireFile = false).ActAsync([missing, noPath], V.Context(logger: logger));

            Assert.All(output, e => Assert.Equal(SyncResult.Created, e.SyncResult));
            Assert.All(_vault.Creates, r => Assert.Empty(r.AllFiles));
            Assert.Contains(logger.Warnings, w => w.Contains("absent.pdf"));
        }

        [Fact]
        public async Task AuditInfo_IsResolvedFromTheLogins()
        {
            var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var entity = V.Entity(("Numéro", "F-1"),
                (MFilesKeys.CreatedBy, "jdoe"), (MFilesKeys.CreationTimeStamp, created),
                (MFilesKeys.LastModifiedBy, "ASMITH"), (MFilesKeys.LastModificationTimeStamp, "2021-06-07T08:09:10Z"));

            await Synchroniser(s => s.SetAuditInfo = true).ActAsync([entity], V.Context());

            var audit = Assert.Single(_vault.Creates).Audit!;
            Assert.Equal(V.JDoe, audit.CreatedBy);
            Assert.Equal(created, audit.CreatedUtc);
            Assert.Equal(V.ASmith, audit.LastModifiedBy);
            Assert.NotNull(audit.LastModifiedUtc);

            // The audit keys are not written as properties.
            Assert.Equal([V.Number], _vault.Creates.Single().Values.Select(v => v.PropertyDefId));
        }

        [Fact]
        public async Task AuditInfo_UnknownLogin_UsesTheDefaultUser_OrWarns()
        {
            var logger = new CapturingLogger();
            var entity = () => V.Entity(("Numéro", Guid.NewGuid().ToString()), (MFilesKeys.CreatedBy, "inconnu"));

            await Synchroniser(s => { s.SetAuditInfo = true; s.DefaultUserId = V.Admin; }).ActAsync([entity()], V.Context());
            Assert.Equal(V.Admin, _vault.Creates.Single().Audit?.CreatedBy);

            await Synchroniser(s => s.SetAuditInfo = true).ActAsync([entity()], V.Context(logger: logger));
            Assert.Null(_vault.Creates.Last().Audit);
            Assert.Contains(logger.Warnings, w => w.Contains("inconnu"));
        }

        [Fact]
        public async Task ObjectIdProperty_ReceivesTheIdOfTheCreatedObject()
        {
            var output = await Synchroniser(s => s.ObjectIdProperty = "mfilesId").ActAsync([Invoice("F-1")], V.Context());

            Assert.Equal((long)_vault.Objects.Single().Id, output[0]["mfilesId"]);
            Assert.DoesNotContain(_vault.Creates.Single().Values, v => v.Value is long);
        }

        [Fact]
        public async Task Session_IsClosedAfterTheRun()
        {
            await Synchroniser().ActAsync([Invoice("F-1")], V.Context());

            Assert.Equal(1, _vault.Calls("Connect"));
            Assert.Equal(1, _vault.Calls("Disconnect"));
            Assert.Equal(1, _vault.Calls("Dispose"));
        }
    }
}
