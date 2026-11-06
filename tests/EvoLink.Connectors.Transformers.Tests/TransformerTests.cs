using System.Security.Cryptography;
using System.Text;
using Ortolan.Core.Conditions;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using static EvoLink.Connectors.Transformers.Tests.TestSupport;

namespace EvoLink.Connectors.Transformers.Tests
{
    public class ArithmeticTransformerTests
    {
        [Theory]
        [InlineData(ArithmeticOperation.Addition, "10", 2, 12)]
        [InlineData(ArithmeticOperation.Subtraction, "10", 2, 8)]
        [InlineData(ArithmeticOperation.Multiplication, "1'234,50", 2, 2469)]
        [InlineData(ArithmeticOperation.Division, "10", 4, 2.5)]
        [InlineData(ArithmeticOperation.Modulus, "10", 3, 1)]
        [InlineData(ArithmeticOperation.Exponentiation, "2", 10, 1024)]
        [InlineData(ArithmeticOperation.Negation, "7.5", 0, -7.5)]
        public async Task Computes_CultureInvariantly(ArithmeticOperation operation, string value, double operand, double expected)
        {
            var transformer = new ArithmeticTransformer { From = "n", Operation = operation, Operand = (decimal)operand, To = "r" };

            var entity = await transformer.TransformAsync(Entity(("n", value)));

            Assert.Equal((decimal)expected, entity!["r"]);
            Assert.Equal(value, entity["n"]);
        }

        [Fact]
        public async Task WithoutTo_ReplacesTheSourceProperty()
        {
            var entity = await new ArithmeticTransformer { From = "n", Operand = 1 }.TransformAsync(Entity(("n", 41L)));

            Assert.Equal(42m, entity!["n"]);
        }

        [Fact]
        public async Task NonNumericOrMissingValue_FailsTheEntity()
        {
            var transformer = new ArithmeticTransformer { From = "n", Operand = 1 };

            Assert.True((await transformer.ActOneAsync(Entity(("n", "abc")))).IsFailed);
            Assert.True((await transformer.ActOneAsync(Entity(("other", "1")))).IsFailed);
        }

        [Fact]
        public async Task IgnoreEmpty_LeavesTheEntityUntouched()
        {
            var entity = await new ArithmeticTransformer { From = "n", Operand = 1, IgnoreEmpty = true, To = "r" }.TransformAsync(Entity(("n", "")));

            Assert.False(entity!.Has("r"));
        }

        [Fact]
        public async Task DivisionByZero_IsAConfigurationError()
        {
            var transformer = new ArithmeticTransformer { From = "n", Operation = ArithmeticOperation.Division, Operand = 0 };

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => transformer.ActOneAsync(Entity(("n", "1"))));
        }
    }

    public class FlowTransformerTests
    {
        [Fact]
        public async Task Composite_PlaysTheTransformersInOrder()
        {
            var composite = new CompositeTransformer
            {
                Transformers =
                [
                    new ArithmeticTransformer { From = "n", Operation = ArithmeticOperation.Multiplication, Operand = 10 },
                    new ArithmeticTransformer { From = "n", Operation = ArithmeticOperation.Addition, Operand = 1 },
                ],
            };

            var entity = await composite.TransformAsync(Entity(("n", "2")));

            Assert.Equal(21m, entity!["n"]);
        }

        [Fact]
        public async Task Composite_ErrorOfASubTransformer_FailsTheEntity()
        {
            var composite = new CompositeTransformer
            {
                Transformers = [new ArithmeticTransformer { From = "missing", Operand = 1 }, new Md5HashTransformer { Properties = ["n"], To = "h" }],
            };

            var entity = await composite.ActOneAsync(Entity(("n", "2")));

            Assert.True(entity.IsFailed);
            Assert.False(entity.Has("h"));
        }

        [Fact]
        public async Task Composite_SubTransformerReturningNull_RemovesTheEntity()
        {
            var composite = new CompositeTransformer { Transformers = [new DropTransformer(), new Md5HashTransformer { Properties = ["n"], To = "h" }] };

            Assert.Null(await composite.TransformAsync(Entity(("n", "2"))));
        }

        [Fact]
        public async Task Composite_DeserializedFromJson_RunsItsPolymorphicSubTransformers()
        {
            var module = FromJson<IModule>("""
                {
                  "$type": "composite",
                  "transformers": [
                    { "$type": "arithmetic", "from": "n", "operation": "Multiplication", "operand": 3 },
                    { "$type": "conditional",
                      "condition": { "$type": "arithmetic-condition", "property": "n", "operator": "GreaterThan", "value": 5 },
                      "transformer": { "$type": "md5-hash", "properties": [ "n" ], "to": "hash" } }
                  ]
                }
                """);

            var composite = Assert.IsType<CompositeTransformer>(module);
            Assert.IsType<ConditionalTransformer>(composite.Transformers[1]);

            var big = await module.ActOneAsync(Entity(("n", "2")));
            var small = await module.ActOneAsync(Entity(("n", "1")));

            Assert.Equal(Md5("6"), big["hash"]);
            Assert.False(small.Has("hash"));
        }

        [Fact]
        public async Task Conditional_AppliesTheTransformerOnlyWhenTheConditionMatches()
        {
            var transformer = new ConditionalTransformer
            {
                Condition = new EqualsCondition { Property = "type", Value = "A" },
                Transformer = new ArithmeticTransformer { From = "n", Operand = 1 },
            };

            Assert.Equal(2m, (await transformer.TransformAsync(Entity(("type", "A"), ("n", "1"))))!["n"]);
            Assert.Equal("1", (await transformer.TransformAsync(Entity(("type", "B"), ("n", "1"))))!["n"]);
        }

        [Fact]
        public async Task Conditional_WithoutCondition_IsAConfigurationError()
        {
            var transformer = new ConditionalTransformer { Transformer = new DropTransformer() };

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => transformer.ActOneAsync(Entity(("n", "1"))));
        }

        [Fact]
        public async Task IfThenElse_ChoosesTheBranch_AndAnEmptyBranchLeavesTheEntityUntouched()
        {
            var transformer = FromJson<IModule>("""
                {
                  "$type": "if-then-else",
                  "condition": { "$type": "contains", "property": "name", "value": "pdf", "ignoreCase": true },
                  "then": { "$type": "aggregate", "property": "name", "mode": "RawValue", "value": "OK", "separator": "-" }
                }
                """);

            var then = await transformer.ActOneAsync(Entity(("name", "doc.PDF")));
            var @else = await transformer.ActOneAsync(Entity(("name", "doc.txt")));

            Assert.Equal("doc.PDF-OK", then["name"]);
            Assert.Equal("doc.txt", @else["name"]);
        }

        [Fact]
        public async Task IfThenElse_AppliesElseWhenTheConditionFails()
        {
            var transformer = new IfThenElseTransformer
            {
                Condition = new EqualsCondition { Property = "type", Value = "A" },
                Then = new ArithmeticTransformer { From = "n", Operand = 1 },
                Else = new ArithmeticTransformer { From = "n", Operation = ArithmeticOperation.Negation },
            };

            Assert.Equal(2m, (await transformer.TransformAsync(Entity(("type", "A"), ("n", "1"))))!["n"]);
            Assert.Equal(-1m, (await transformer.TransformAsync(Entity(("type", "B"), ("n", "1"))))!["n"]);
        }

        [Fact]
        public void Configuration_RoundTripsWithTheAliases()
        {
            var registry = Registry();
            var composite = new CompositeTransformer { Transformers = [new Md5HashTransformer { Properties = ["a"], To = "h" }] };

            var json = Ortolan.Core.Serialization.OrtolanJson.FromConfiguration(composite, registry).ToString();

            Assert.Contains("\"$type\": \"md5-hash\"", json);
            Assert.IsType<Md5HashTransformer>(FromJson<CompositeTransformer>(json).Transformers[0]);
        }

        internal static string Md5(string text)
            => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

        private sealed class DropTransformer : TransformerModule
        {
            public override Task<Entity?> TransformAsync(Entity entity, Ortolan.Core.Pipeline.JobContext context)
                => Task.FromResult<Entity?>(null);
        }
    }

    public class CsvLookupTransformerTests
    {
        [Fact]
        public async Task CopiesTheValueOfTheMatchingRow()
        {
            using var folder = new TempFolder();
            folder.File("table.csv", "Code;Label;Other\r\nA1;\"Alpha; first\";x\r\nB2;\"Beta \"\"quoted\"\"\";y\r\nA1;Alpha bis;z\r\n");

            var transformer = new CsvLookupTransformer { From = "code", To = "label", FilePath = "table.csv", MatchingColumn = "code", DataColumn = "Label" };
            var context = Context(folder.Path);

            var a = await transformer.ActOneAsync(Entity(("code", " A1 ")), context);
            var b = await transformer.ActOneAsync(Entity(("code", "B2")), context);

            Assert.Equal("Alpha bis", a["label"]);
            Assert.Equal("Beta \"quoted\"", b["label"]);
        }

        [Fact]
        public async Task NoMatch_WarnsOrFails()
        {
            using var folder = new TempFolder();
            var path = folder.File("table.csv", "Code;Label\nA1;Alpha\n");
            var context = Context();

            var lenient = await new CsvLookupTransformer { From = "code", To = "label", FilePath = path, MatchingColumn = "Code", DataColumn = "Label" }
                .ActOneAsync(Entity(("code", "a1")), context);
            var strict = await new CsvLookupTransformer { From = "code", To = "label", FilePath = path, MatchingColumn = "Code", DataColumn = "Label", FailWhenNotFound = true }
                .ActOneAsync(Entity(("code", "Z9")), context);
            var ignoreCase = await new CsvLookupTransformer { From = "code", To = "label", FilePath = path, MatchingColumn = "Code", DataColumn = "Label", IgnoreCase = true }
                .ActOneAsync(Entity(("code", "a1")), context);

            Assert.False(lenient.IsFailed);
            Assert.False(lenient.Has("label"));
            Assert.Equal(1, context.Report.Warnings);
            Assert.True(strict.IsFailed);
            Assert.Equal("Alpha", ignoreCase["label"]);
        }

        [Fact]
        public async Task ReadsWindows1252()
        {
            using var folder = new TempFolder();
            var path = Path.Combine(folder.Path, "ansi.csv");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            await File.WriteAllTextAsync(path, "Code,Label\n1,Genève\n", Encoding.GetEncoding(1252));

            var entity = await new CsvLookupTransformer { From = "code", To = "label", FilePath = path, MatchingColumn = "Code", DataColumn = "Label", Delimiter = ",", Encoding = "windows-1252" }
                .TransformAsync(Entity(("code", 1L)));

            Assert.Equal("Genève", entity!["label"]);
        }

        [Fact]
        public async Task MissingFileOrColumn_IsAConfigurationError()
        {
            using var folder = new TempFolder();
            var path = folder.File("table.csv", "Code;Label\nA1;Alpha\n");

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => new CsvLookupTransformer { From = "c", To = "l", FilePath = Path.Combine(folder.Path, "none.csv"), MatchingColumn = "Code", DataColumn = "Label" }
                .ActOneAsync(Entity(("c", "A1"))));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => new CsvLookupTransformer { From = "c", To = "l", FilePath = path, MatchingColumn = "Code", DataColumn = "Missing" }
                .ActOneAsync(Entity(("c", "A1"))));
        }
    }

    public class PathTransformerTests
    {
        [Theory]
        [InlineData(@"C:\Data\Clients\ACME\2024\facture.pdf", @"C:\Data", 1, "Clients")]
        [InlineData(@"C:\Data\Clients\ACME\2024\facture.pdf", @"c:/data/", 2, "ACME")]
        [InlineData(@"C:\Data\Clients\ACME\2024\facture.pdf", null, 2, "Data")]
        [InlineData(@"\\server\share\Clients\facture.pdf", @"\\server\share", -1, "facture.pdf")]
        [InlineData("/mnt/data/Clients/ACME/facture.pdf", "/mnt/data", -2, "ACME")]
        public async Task PathSegment_ExtractsTheSegment(string path, string? root, int depth, string expected)
        {
            var entity = await new PathSegmentTransformer { From = "path", To = "segment", RootPath = root, Depth = depth }.TransformAsync(Entity(("path", path)));

            Assert.Equal(expected, entity!["segment"]);
        }

        [Fact]
        public async Task PathSegment_OutOfRangeOrOutsideTheRoot_FailsTheEntity()
        {
            Assert.True((await new PathSegmentTransformer { From = "p", To = "s", Depth = 5 }.ActOneAsync(Entity(("p", @"C:\a\b.txt")))).IsFailed);
            Assert.True((await new PathSegmentTransformer { From = "p", To = "s", RootPath = @"D:\x" }.ActOneAsync(Entity(("p", @"C:\a\b.txt")))).IsFailed);
            Assert.True((await new PathSegmentTransformer { From = "p", To = "s", RootPath = @"C:\Dat" }.ActOneAsync(Entity(("p", @"C:\Data\b.txt")))).IsFailed);
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => new PathSegmentTransformer { From = "p", To = "s", Depth = 0 }.ActOneAsync(Entity(("p", @"C:\a\b.txt"))));
        }

        [Fact]
        public async Task FileInfo_WithoutRelativeRoot_UsesTheFullPath()
        {
            using var folder = new TempFolder();
            var path = folder.File(@"Clients/ACME/rapport.final.pdf", new string('x', 1536));

            var entity = await new FileInfoTransformer().TransformAsync(Entity((FileKeys.FilePath, path.Replace('\\', '/'))));

            Assert.Equal(path, entity![FileKeys.FilePath]);
            Assert.Equal(path, entity[FileKeys.RelativePath]);
            Assert.Equal(1536L, entity[FileKeys.ByteSize]);
            Assert.Equal("1.5 KB", entity[FileKeys.ReadableSize]);
            Assert.Equal("rapport.final.pdf", entity[FileKeys.Name]);
            Assert.Equal("rapport.final", entity[FileKeys.NameWithoutExtension]);
            Assert.Equal("rapport.final", entity[FileKeys.ExcludedName]);
            Assert.Equal(".pdf", entity[FileKeys.Extension]);
            Assert.Equal(DateTime.Today, entity[FileKeys.LastModificationTimeStamp]);
            Assert.IsType<DateTime>(entity[FileKeys.CreationTimeStamp]);
            Assert.Equal(Path.GetRelativePath(Path.GetPathRoot(path)!, path).Split(Path.DirectorySeparatorChar).Length - 1, entity[FileKeys.NumberParentFolders]);
        }

        [Fact]
        public async Task FileInfo_WithRelativeRootAndExclusions()
        {
            using var folder = new TempFolder();
            var path = folder.File(@"Clients/ACME/contrat.pdf.p7m", "abc");

            var transformer = new FileInfoTransformer { FilePathProperty = "path", RelativeRoot = folder.Path + Path.DirectorySeparatorChar, ExtensionExclusions = ["p7m", ".tmp"] };
            var entity = await transformer.TransformAsync(Entity(("path", path)));

            Assert.Equal(Path.Combine("Clients", "ACME", "contrat.pdf.p7m"), entity![FileKeys.RelativePath]);
            Assert.Equal(2, entity[FileKeys.NumberParentFolders]);
            Assert.Equal("contrat", entity[FileKeys.ExcludedName]);
            Assert.Equal(".pdf", entity[FileKeys.Extension]);
            Assert.Equal(".p7m", entity[FileKeys.ExcludedExtension]);
            Assert.Equal("p7m;.tmp", entity[FileKeys.Exclusions]);
            Assert.Equal("3 B", entity[FileKeys.ReadableSize]);
        }

        [Fact]
        public async Task FileInfo_MissingFile_FailsOrOnlyUsesThePath()
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.txt");

            Assert.True((await new FileInfoTransformer().ActOneAsync(Entity((FileKeys.FilePath, missing)))).IsFailed);

            var lenient = await new FileInfoTransformer { RequireFile = false }.ActOneAsync(Entity((FileKeys.FilePath, missing)));
            Assert.False(lenient.IsFailed);
            Assert.Equal("absent.txt", lenient[FileKeys.Name]);
            Assert.False(lenient.Has(FileKeys.ByteSize));
        }

        [Fact]
        public async Task FileInfo_EmptyPathOrOutsideTheRoot_FailsOnlyWhenTheFileIsRequired()
        {
            Assert.True((await new FileInfoTransformer().ActOneAsync(Entity((FileKeys.FilePath, " ")))).IsFailed);

            var empty = await new FileInfoTransformer { RequireFile = false }.ActOneAsync(Entity((FileKeys.FilePath, "")));
            Assert.False(empty.IsFailed);
            Assert.False(empty.Has(FileKeys.Name));

            var noProperty = await new FileInfoTransformer { RequireFile = false }.ActOneAsync(Entity(("autre", "x")));
            Assert.False(noProperty.IsFailed);

            var outside = Path.Combine(Path.GetTempPath(), "ailleurs", "a.txt");
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Assert.True((await new FileInfoTransformer { RelativeRoot = root }.ActOneAsync(Entity((FileKeys.FilePath, outside)))).IsFailed);

            var lenient = await new FileInfoTransformer { RelativeRoot = root, RequireFile = false }.ActOneAsync(Entity((FileKeys.FilePath, outside)));
            Assert.False(lenient.IsFailed);
            Assert.Equal(outside, lenient[FileKeys.RelativePath]);
        }

        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1024L, "1 KB")]
        [InlineData(1599L, "1.56 KB")]
        [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
        public void ReadableSize_IsTruncatedAndInvariant(long bytes, string expected)
            => Assert.Equal(expected, FileInfoTransformer.ToReadableSize(bytes));

        [Fact]
        public void LastModifiedByKey_IsFixed()
            => Assert.Equal("FILE-LastModifiedBy", FileKeys.LastModifiedBy);
    }

    public class TextTransformerTests
    {
        [Fact]
        public async Task Md5_ConcatenatesInTheConfiguredOrder_Invariantly()
        {
            var entity = Entity(("b", "lo"), ("a", "hel"), ("n", 1.5m), ("d", new DateTime(2024, 3, 1)));

            await new Md5HashTransformer { Properties = ["a", "b"], To = "h1" }.TransformAsync(entity);
            await new Md5HashTransformer { Properties = ["n", "d"], To = "h2", Separator = "|" }.TransformAsync(entity);

            Assert.Equal("5d41402abc4b2a76b9719d911017c592", entity["h1"]);
            Assert.Equal(FlowTransformerTests.Md5("1.5|2024-03-01"), entity["h2"]);
        }

        [Fact]
        public async Task Md5_MissingProperty_FailsTheEntity()
            => Assert.True((await new Md5HashTransformer { Properties = ["a", "x"], To = "h" }.ActOneAsync(Entity(("a", "1")))).IsFailed);

        [Fact]
        public async Task Aggregate_AppendsAConstantOrAProperty()
        {
            var raw = await new AggregateTransformer { Property = "name", Mode = AggregationMode.RawValue, Value = "suffix", Separator = "_" }.TransformAsync(Entity(("name", "doc")));
            var prop = await new AggregateTransformer { Property = "name", SourceProperty = "year", Separator = " - " }.TransformAsync(Entity(("name", "doc"), ("year", 2024L)));
            var empty = await new AggregateTransformer { Property = "name", SourceProperty = "year", Separator = ";", IgnoreEmpty = true }.TransformAsync(Entity(("name", ""), ("year", "2024")));

            Assert.Equal("doc_suffix", raw!["name"]);
            Assert.Equal("doc - 2024", prop!["name"]);
            Assert.Equal("2024", empty!["name"]);
            Assert.True((await new AggregateTransformer { Property = "name", SourceProperty = "x" }.ActOneAsync(Entity(("name", "doc")))).IsFailed);
        }

        [Fact]
        public async Task RegexAggregate_JoinsMatchesOrGroups()
        {
            var matches = await new RegexAggregateTransformer { From = "t", Pattern = @"\d+", Separator = "," }.TransformAsync(Entity(("t", "a1b22c333")));
            var groups = await new RegexAggregateTransformer { From = "t", To = "r", Pattern = @"(\w)-(\d)", Separator = "", UseGroups = true }.TransformAsync(Entity(("t", "A-1 B-2")));
            var none = await new RegexAggregateTransformer { From = "t", Pattern = "x" }.TransformAsync(Entity(("t", "abc")));

            Assert.Equal("1,22,333", matches!["t"]);
            Assert.Equal("A1B2", groups!["r"]);
            Assert.Equal("A-1 B-2", groups["t"]);
            Assert.Equal(string.Empty, none!["t"]);
        }

        [Fact]
        public async Task RegexAggregate_InvalidPattern_IsAConfigurationError_AndMissingPropertyFails()
        {
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => new RegexAggregateTransformer { From = "t", Pattern = "(" }.ActOneAsync(Entity(("t", "a"))));
            Assert.True((await new RegexAggregateTransformer { From = "t", Pattern = "a" }.ActOneAsync(Entity(("x", "a")))).IsFailed);
        }

        [Fact]
        public async Task RegexExtract_CopiesTheFirstMatchOrGroup()
        {
            var whole = await new RegexExtractTransformer { From = "t", To = "r", Pattern = @"\d{4}" }.TransformAsync(Entity(("t", "Facture 2023 / 2024")));
            var named = await new RegexExtractTransformer { From = "t", To = "r", Pattern = @"N°\s*(?<num>\d+)", Group = "num", IgnoreCase = true }.TransformAsync(Entity(("t", "n° 42")));
            var none = await new RegexExtractTransformer { From = "t", To = "r", Pattern = @"\d" }.TransformAsync(Entity(("t", "abc"), ("r", "keep")));
            var missing = await new RegexExtractTransformer { From = "t", To = "r", Pattern = @"\d" }.TransformAsync(Entity(("x", "1")));

            Assert.Equal("2023", whole!["r"]);
            Assert.Equal("42", named!["r"]);
            Assert.Equal("keep", none!["r"]);
            Assert.False(missing!.Has("r"));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => new RegexExtractTransformer { From = "t", To = "r", Pattern = @"\d", Group = "nope" }.ActOneAsync(Entity(("t", "1"))));
        }

        [Fact]
        public async Task Regex_CatastrophicBacktracking_TimesOutAndFailsTheEntity()
        {
            var transformer = new RegexExtractTransformer { From = "t", To = "r", Pattern = @"^(a+)+$", TimeoutMs = 50 };

            var entity = await transformer.ActOneAsync(Entity(("t", new string('a', 40) + "!")));

            Assert.True(entity.IsFailed);
        }
    }
}
