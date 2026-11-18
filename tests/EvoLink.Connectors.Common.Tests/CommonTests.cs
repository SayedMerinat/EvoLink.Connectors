using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.Common.Tests
{
    public class ValueConverterTests
    {
        [Theory]
        [InlineData("12,50", 12.5)]
        [InlineData("1'234.75", 1234.75)]
        [InlineData("1,234.75", 1234.75)]
        [InlineData("-3", -3)]
        [InlineData(" 7 ", 7)]
        public void ToDecimal_AcceptsSwissAndInvariantFormats(string text, double expected)
            => Assert.Equal((decimal)expected, ValueConverter.ToDecimal(text));

        [Theory]
        [InlineData("06.06.2017")]
        [InlineData("2017-06-06")]
        [InlineData("06/06/2017")]
        [InlineData("20170606")]
        public void ToDateTime_AcceptsTheUsualFormats(string text)
            => Assert.Equal(new DateTime(2017, 6, 6), ValueConverter.ToDateTime(text));

        /// <summary>
        /// A date serialised then read back (checkpoint of a resumed job, JSON) is the same date: an UTC date is not converted
        /// to the local time, otherwise the resumed job writes every date shifted by the UTC offset.
        /// </summary>
        [Fact]
        public void ToDateTime_ASerialisedDate_IsReadBackUnchanged()
        {
            var utc = new DateTime(2024, 9, 11, 10, 2, 4, DateTimeKind.Utc);
            var local = new DateTime(2024, 9, 11, 10, 2, 4, DateTimeKind.Local);
            var unspecified = new DateTime(2024, 9, 11, 10, 2, 4);

            foreach (var date in new[] { utc, local, unspecified })
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(date).Trim('"');
                var read = ValueConverter.ToDateTime(json);

                Assert.Equal(date, read);
                Assert.Equal(date.Kind, read!.Value.Kind);
            }

            Assert.Equal(utc, ValueConverter.ToDateTime("2024-09-11T10:02:04Z"));
            Assert.Equal(DateTimeKind.Utc, ValueConverter.ToDateTime("2024-09-11T10:02:04.0000000Z")!.Value.Kind);
            Assert.Equal(DateTimeKind.Unspecified, ValueConverter.ToDateTime("2024-09-11 10:02:04")!.Value.Kind);
        }

        [Fact]
        public void Conversions_ReturnNullInsteadOfThrowing()
        {
            Assert.Null(ValueConverter.ToDecimal("abc"));
            Assert.Null(ValueConverter.ToInt64("1.5"));
            Assert.Null(ValueConverter.ToDateTime("31.02.2017"));
            Assert.Null(ValueConverter.ToBoolean("peut-être"));
            Assert.Equal(true, ValueConverter.ToBoolean("Oui"));
            Assert.Equal(false, ValueConverter.ToBoolean("0"));
            Assert.Equal(42L, ValueConverter.ToInt64("42.0"));
            Assert.Equal("2017-06-06", ValueConverter.ToText(new DateTime(2017, 6, 6)));
            Assert.Equal(["a", "b"], ValueConverter.ToList("a; b;"));
        }
    }

    public class FieldMappingTests
    {
        [Fact]
        public void Mapping_RenamesInBothDirections_AndCanLimitTheFields()
        {
            var entity = new Entity { ["id"] = 1L, ["nom"] = "Alice", ["interne"] = "x" };

            var all = new FieldMapping(new Dictionary<string, string> { ["nom"] = "Name" });
            Assert.Equal([("id", (object?)1L), ("Name", "Alice"), ("interne", "x")], all.ToFields(entity));

            var only = new FieldMapping(new Dictionary<string, string> { ["id"] = "Id", ["nom"] = "Name" }, onlyMappedFields: true);
            Assert.Equal([("Id", (object?)1L), ("Name", "Alice")], only.ToFields(entity));

            var back = only.ToEntity([new("Id", 2L), new("Name", "Bob"), new("Other", "ignored")]);
            Assert.Equal(["id", "nom"], back.Properties.Keys);
        }

        [Fact]
        public void Required_ListsTheMissingProperties()
        {
            var ex = Assert.Throws<OrtolanConfigurationException>(() => ConnectorGuard.Required("m", ("Url", ""), ("Port", 0), ("Model", "res.partner")));
            Assert.Contains("Url, Port", ex.Message);
        }
    }
}
