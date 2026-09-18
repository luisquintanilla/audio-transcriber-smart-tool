using AudioTranscriber.Cli;

return await new CliApplication().RunAsync(
    args,
    Console.Out,
    Console.Error,
    Directory.GetCurrentDirectory());
