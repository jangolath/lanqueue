using YamlDotNet.Serialization;

namespace LanQueue.Config;

/// <summary>
/// Compile-time YAML type information for the config (YamlDotNet's static generator), used by every build: no reflection, so the
/// framework-dependent tool and the trimmed NativeAOT binary load configs through exactly the same code.
/// </summary>
[YamlStaticContext]
[YamlSerializable(typeof(FleetConfig))]
[YamlSerializable(typeof(PolicyConfig))]
[YamlSerializable(typeof(Machine))]
public partial class YamlContext : StaticContext;
