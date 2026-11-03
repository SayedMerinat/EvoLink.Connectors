using System.ComponentModel;
using System.Globalization;
using EvoLink.Connectors.Common;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers
{
    public enum ArithmeticOperation
    {
        Addition,
        Subtraction,
        Multiplication,
        Division,
        Modulus,
        Exponentiation,
        Negation,
    }

    /// <summary>
    /// Port of the EvoBus <c>ArithmeticTransformer</c>: applies an operation between a numeric property and a constant.
    /// Differences: the value is parsed culture-invariantly (tolerant to "1'234,50"), the result is always a decimal, a
    /// missing or non numeric value fails the entity instead of being logged, a division by zero is a configuration error.
    /// </summary>
    [OrtolanType("arithmetic")]
    [Description("Applique une opération arithmétique entre une propriété numérique et une constante.")]
    public class ArithmeticTransformer : TransformerModule
    {
        [Description("Propriété numérique d'origine.")]
        public string From { get; set; } = string.Empty;

        [Description("Opération : Addition, Subtraction, Multiplication, Division, Modulus, Exponentiation, Negation.")]
        public ArithmeticOperation Operation { get; set; } = ArithmeticOperation.Addition;

        [Description("Opérande constant (ignoré pour Negation).")]
        public decimal Operand { get; set; }

        [Description("Propriété recevant le résultat (vide = From).")]
        public string? To { get; set; }

        [Description("Laisse l'entité inchangée lorsque la propriété est absente ou vide, au lieu de la mettre en échec.")]
        public bool IgnoreEmpty { get; set; }

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("from", From));

            if (Operand == 0m && Operation is ArithmeticOperation.Division or ArithmeticOperation.Modulus)
                throw ConnectorGuard.Invalid(DisplayName, $"Opération {Operation} avec un opérande égal à 0.");

            var raw = entity.GetValueOrDefault(From);

            if (ValueConverter.IsEmpty(raw))
            {
                if (IgnoreEmpty)
                    return Task.FromResult<Entity?>(entity);

                Helpers.GetRequired(entity, From);
                throw new InvalidOperationException($"La propriété '{From}' est vide.");
            }

            var value = ValueConverter.ToDecimal(raw)
                ?? throw new FormatException($"La valeur '{Helpers.Text(raw)}' de la propriété '{From}' n'est pas numérique.");

            entity[string.IsNullOrWhiteSpace(To) ? From : To] = Compute(value);

            return Task.FromResult<Entity?>(entity);
        }

        private decimal Compute(decimal value)
            => Operation switch
            {
                ArithmeticOperation.Addition => value + Operand,
                ArithmeticOperation.Subtraction => value - Operand,
                ArithmeticOperation.Multiplication => value * Operand,
                ArithmeticOperation.Division => value / Operand,
                ArithmeticOperation.Modulus => value % Operand,
                ArithmeticOperation.Exponentiation => Power(value, Operand),
                ArithmeticOperation.Negation => -value,
                _ => throw new NotSupportedException($"Opération '{Operation}' non prise en charge."),
            };

        private static decimal Power(decimal value, decimal exponent)
        {
            var result = Math.Pow((double)value, (double)exponent);

            if (!double.IsFinite(result) || Math.Abs(result) >= 7.9e28)
                throw new OverflowException(string.Create(CultureInfo.InvariantCulture, $"Le résultat de {value} ^ {exponent} n'est pas représentable."));

            return Convert.ToDecimal(result);
        }
    }
}
