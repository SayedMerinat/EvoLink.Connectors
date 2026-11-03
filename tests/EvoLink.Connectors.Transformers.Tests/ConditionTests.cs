using EvoLink.Connectors.Transformers.Conditions;
using Ortolan.Core.Conditions;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using static EvoLink.Connectors.Transformers.Tests.TestSupport;

namespace EvoLink.Connectors.Transformers.Tests
{
    public class ConditionTests
    {
        [Theory]
        [InlineData("1'000.5", NumericComparison.GreaterThan, 1000, true)]
        [InlineData("1000,5", NumericComparison.LessThanOrEqual, 1000, false)]
        [InlineData("12", NumericComparison.Equals, 12, true)]
        [InlineData("12.0", NumericComparison.NotEquals, 12, false)]
        [InlineData("3", NumericComparison.LessThan, 4, true)]
        [InlineData("4", NumericComparison.GreaterThanOrEqual, 4, true)]
        public void Arithmetic_ComparesNumbersInvariantly(string value, NumericComparison comparison, double expected, bool result)
            => Assert.Equal(result, new ArithmeticCondition { Property = "n", Operator = comparison, Value = (decimal)expected }.Match(Entity(("n", value))));

        [Fact]
        public void Arithmetic_InvalidValue_ThrowsOrUsesResultWhenInvalid()
        {
            Assert.Throws<InvalidOperationException>(() => new ArithmeticCondition { Property = "n" }.Match(Entity(("n", "abc"))));
            Assert.Throws<InvalidOperationException>(() => new ArithmeticCondition { Property = "n" }.Match(Entity()));
            Assert.False(new ArithmeticCondition { Property = "n", ResultWhenInvalid = false }.Match(Entity(("n", "abc"))));
            Assert.True(new ArithmeticCondition { Property = "n", ResultWhenInvalid = true }.Match(Entity()));
            Assert.Throws<OrtolanConfigurationException>(() => new ArithmeticCondition().Match(Entity(("n", "1"))));
        }

        [Fact]
        public void Date_ComparesFixedAndRelativeDates()
        {
            var old = Entity(("d", DateTime.Today.AddDays(-40).ToString("dd.MM.yyyy")));
            var recent = Entity(("d", DateTime.Now.AddHours(-1)));

            var olderThan30Days = new DateCondition { Property = "d", Operator = NumericComparison.LessThan, Value = "today-30" };
            Assert.True(olderThan30Days.Match(old));
            Assert.False(olderThan30Days.Match(recent));

            Assert.True(new DateCondition { Property = "d", Operator = NumericComparison.Equals, Value = "2024-03-01", DateOnly = true }.Match(Entity(("d", "01.03.2024 14:30"))));
            Assert.False(new DateCondition { Property = "d", Operator = NumericComparison.Equals, Value = "2024-03-01" }.Match(Entity(("d", "2024-03-01 14:30"))));
            Assert.True(new DateCondition { Property = "d", Operator = NumericComparison.GreaterThan, Value = "now - 2" }.Match(recent));
        }

        [Fact]
        public void Date_ResolvesRelativeExpressions_AndRejectsInvalidOnes()
        {
            var now = new DateTime(2024, 5, 10, 15, 30, 0);

            Assert.Equal(new DateTime(2024, 5, 10), DateCondition.ResolveExpected("TODAY", now));
            Assert.Equal(new DateTime(2024, 5, 17, 15, 30, 0), DateCondition.ResolveExpected("now+7", now));
            Assert.Equal(new DateTime(2024, 4, 10), DateCondition.ResolveExpected("today-30", now));
            Assert.Throws<OrtolanConfigurationException>(() => DateCondition.ResolveExpected("hier", now));
            Assert.Throws<InvalidOperationException>(() => new DateCondition { Property = "d" }.Match(Entity(("d", "pas une date"))));
        }

        [Fact]
        public void FileSize_ComparesTheExactSizeInTheConfiguredUnit()
        {
            var entity = Entity((FileKeys.ByteSize, 1_572_864L)); // 1.5 MB

            Assert.True(new FileSizeCondition { Size = 1 }.Match(entity));
            Assert.False(new FileSizeCondition { Size = 1, Operator = NumericComparison.Equals }.Match(entity));
            Assert.True(new FileSizeCondition { Size = 1536, Unit = SizeUnit.KB, Operator = NumericComparison.Equals }.Match(entity));
            Assert.True(new FileSizeCondition { Size = 2, Operator = NumericComparison.LessThan }.Match(entity));
            Assert.Throws<InvalidOperationException>(() => new FileSizeCondition().Match(Entity()));
            Assert.False(new FileSizeCondition { ResultWhenInvalid = false }.Match(Entity()));
        }

        [Fact]
        public void Contains_IsCaseSensitiveByDefault_AndFalseOnEmpty()
        {
            Assert.True(new ContainsCondition { Property = "t", Value = "Fact" }.Match(Entity(("t", "Facture"))));
            Assert.False(new ContainsCondition { Property = "t", Value = "fact" }.Match(Entity(("t", "Facture"))));
            Assert.True(new ContainsCondition { Property = "t", Value = "fact", IgnoreCase = true }.Match(Entity(("t", "Facture"))));
            Assert.False(new ContainsCondition { Property = "t", Value = "" }.Match(Entity(("t", ""))));
            Assert.False(new ContainsCondition { Property = "t", Value = "x" }.Match(Entity()));
        }

        [Theory]
        [InlineData(TextComparison.Equals, "ABC", false, false)]
        [InlineData(TextComparison.Equals, "ABC", true, true)]
        [InlineData(TextComparison.NotEquals, "abc", false, false)]
        [InlineData(TextComparison.Contains, "b", false, true)]
        [InlineData(TextComparison.NotContains, "B", false, true)]
        [InlineData(TextComparison.NotContains, "B", true, false)]
        public void PropertyCompare_ComparesTexts(TextComparison comparison, string value, bool ignoreCase, bool expected)
            => Assert.Equal(expected, new PropertyCompareCondition { Property = "t", Operator = comparison, Value = value, IgnoreCase = ignoreCase }.Match(Entity(("t", "abc"))));

        [Fact]
        public void PropertyCompare_UsesInvariantTexts_AndHandlesMissingProperties()
        {
            Assert.True(new PropertyCompareCondition { Property = "n", Value = "1.5" }.Match(Entity(("n", 1.5m))));
            Assert.True(new PropertyCompareCondition { Property = "n", Value = "" }.Match(Entity(("n", null))));
            Assert.Throws<InvalidOperationException>(() => new PropertyCompareCondition { Property = "x" }.Match(Entity()));
            Assert.True(new PropertyCompareCondition { Property = "x", ResultWhenInvalid = true }.Match(Entity()));
        }

        [Fact]
        public void Conditions_CanBeUsedInTheCriteriaOfAService()
        {
            var sync = FromJson<SyncOptions>("""
                {
                  "keyFields": [ "id" ],
                  "criteria": [
                    { "$type": "file-size", "operator": "LessThan", "size": 10, "unit": "KB", "resultWhenInvalid": false },
                    { "$type": "not", "condition": { "$type": "contains", "property": "name", "value": "~$" } },
                    { "$type": "or", "conditions": [
                      { "$type": "property-compare", "property": "type", "operator": "Equals", "value": "PDF", "ignoreCase": true },
                      { "$type": "date-condition", "property": "date", "operator": "GreaterThanOrEqual", "value": "2024-01-01", "resultWhenInvalid": false },
                      { "$type": "arithmetic-condition", "property": "pages", "operator": "GreaterThan", "value": 100, "resultWhenInvalid": false }
                    ] }
                  ]
                }
                """);

            Assert.Equal(3, sync.Criteria.Count);
            Assert.True(sync.IsTakenIntoAccount(Entity((FileKeys.ByteSize, 2048L), ("name", "doc.pdf"), ("type", "pdf"))));
            Assert.True(sync.IsTakenIntoAccount(Entity((FileKeys.ByteSize, 2048L), ("name", "doc.txt"), ("type", "txt"), ("date", "15.02.2024"))));
            Assert.False(sync.IsTakenIntoAccount(Entity((FileKeys.ByteSize, 2048L), ("name", "~$doc.pdf"), ("type", "pdf"))));
            Assert.False(sync.IsTakenIntoAccount(Entity((FileKeys.ByteSize, 20480L), ("name", "doc.pdf"), ("type", "pdf"))));
            Assert.False(sync.IsTakenIntoAccount(Entity((FileKeys.ByteSize, 2048L), ("name", "doc.txt"), ("type", "txt"), ("pages", "12"))));
        }

        [Fact]
        public void EveryAlias_IsRegistered()
        {
            var registry = Registry();
            string[] aliases =
            [
                "arithmetic", "conditional", "if-then-else", "composite", "csv-lookup", "path-segment", "md5-hash", "file-info",
                "aggregate", "regex-aggregate", "regex-extract",
                "arithmetic-condition", "date-condition", "file-size", "contains", "property-compare",
            ];

            foreach (var alias in aliases)
                Assert.True(registry.TryGetType(alias, out _), $"Alias '{alias}' non enregistré.");

            Assert.All(registry.Types.Where(t => t.Type.Assembly == typeof(CompositeTransformer).Assembly), t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
            Assert.True(registry.TryGetType("composite", out var composite) && typeof(Ortolan.Core.Modules.ITransformer).IsAssignableFrom(composite));
            Assert.True(registry.TryGetType("contains", out var contains) && typeof(ICondition).IsAssignableFrom(contains));
        }
    }
}
