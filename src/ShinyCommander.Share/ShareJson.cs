using System.Text.Json.Serialization;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.Share;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ShareInfo))]
[JsonSerializable(typeof(ShareListing))]
[JsonSerializable(typeof(ShareKeyRequest))]
[JsonSerializable(typeof(ShareFolderRequest))]
[JsonSerializable(typeof(ShareRenameRequest))]
[JsonSerializable(typeof(ShareUploaded))]
[JsonSerializable(typeof(ShareError))]
partial class ShareJson : JsonSerializerContext;
