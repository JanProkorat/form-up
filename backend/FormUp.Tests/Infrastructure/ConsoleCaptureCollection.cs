namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Runs alone because <see cref="ConsoleOutputCapture"/> swaps the process-wide <see cref="Console.Out"/>;
/// it still gets its own <see cref="FitnessApiFactory"/>.
/// </summary>
[CollectionDefinition("ConsoleCapture", DisableParallelization = true)]
public class ConsoleCaptureCollection : ICollectionFixture<FitnessApiFactory>;
