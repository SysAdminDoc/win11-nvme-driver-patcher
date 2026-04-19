using System.Globalization;
using System.Windows.Data;
using NVMeDriverPatcher.Converters;

namespace NVMeDriverPatcher.Tests;

public sealed class StatusToColorConverterTests
{
    public static IEnumerable<object[]> OneWayConverters =>
    [
        [new StatusToColorConverter()],
        [new BoolToVisibilityConverter()],
        [new SettingsToggleConverter()],
        [new StringToColorConverter()],
        [new StringToBrushConverter()]
    ];

    [Theory]
    [MemberData(nameof(OneWayConverters))]
    public void OneWayConverters_ReturnDoNothingFromConvertBack(IValueConverter converter)
    {
        var result = converter.ConvertBack(new object(), typeof(object), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Same(Binding.DoNothing, result);
    }
}
