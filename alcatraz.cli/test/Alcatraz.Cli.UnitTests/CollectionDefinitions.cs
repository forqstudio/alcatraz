namespace Alcatraz.Cli.UnitTests;

// Tests that mutate XDG_CONFIG_HOME / APPDATA share this collection so xUnit runs
// them serially even when test parallelism is otherwise on.
[CollectionDefinition("ConfigPath", DisableParallelization = true)]
public class ConfigPathCollection { }
