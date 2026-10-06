using System.Text;

namespace NVMeDriverPatcher.Services;

// Redirected output used to go out in the OEM code page, which has no arrow, so the CLI's
// `dry-run > plan.md` printed "Before  After". The CLI and the Watchdog exe call this first thing,
// and redirected streams get UTF-8 without a BOM. The writers are swapped rather than setting
// Console.OutputEncoding, because that calls SetConsoleOutputCP and would leave the parent shell's
// console on a different code page after the process exits.
public static class RedirectedConsoleEncoding
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void UseUtf8WhenRedirected()
    {
        if (Console.IsOutputRedirected)
            Console.SetOut(CreateWriter(Console.OpenStandardOutput()));
        if (Console.IsErrorRedirected)
            Console.SetError(CreateWriter(Console.OpenStandardError()));
    }

    // AutoFlush keeps stdout and stderr in order and loses nothing when a command returns early.
    public static TextWriter CreateWriter(Stream stream) =>
        new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
}
