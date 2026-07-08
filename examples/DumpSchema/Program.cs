using Vortex;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: DumpSchema <file.vortex>");
    Console.Error.WriteLine("Set VORTEX_FFI_PATH to the vortex_ffi native library if it is not on the default search path.");
    return 1;
}

using var file = VortexFile.Open(args[0]);

Console.WriteLine("schema:");
foreach (var field in file.Schema.FieldsList)
    Console.WriteLine($"  {field.Name}: {field.DataType} nullable={field.IsNullable}");

long rows = 0;
int batches = 0;
foreach (var batch in file.ReadAll())
{
    rows += batch.Length;
    batches++;
    batch.Dispose();
}

Console.WriteLine($"{batches} batches, {rows} rows");
return 0;
