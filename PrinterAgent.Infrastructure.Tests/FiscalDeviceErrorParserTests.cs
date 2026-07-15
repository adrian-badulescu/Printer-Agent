using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FiscalDeviceErrorParserTests
{
    [Theory]
    [InlineData("Tremol error 101 server conection error", "TREMOL_101", "101")]
    [InlineData("ErrCode: 30 – ServSockConnectionFailedSocket connect FAILED", "TREMOL_30", "30")]
    [InlineData("\"xC3\": \"ef_NoFisPrnMode Mod imprimanta fiscala neactivat\"", "ORGTECH_xC3", "xC3")]
    [InlineData("Datecs cod eroare 33022 – Device not conected", "DATECS_33022", "33022")]
    [InlineData("Daisy cod eroare 82 – Au trecut mai mult de 24 ore", "DAISY_82", "82")]
    public void TryParse_maps_vendor_codes(string body, string expectedCode, string expectedDevice)
    {
        var parsed = FiscalDeviceErrorParser.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal(expectedCode, parsed!.ErrorCode);
        Assert.Equal(expectedDevice, parsed.DeviceErrorCode);
    }
}
