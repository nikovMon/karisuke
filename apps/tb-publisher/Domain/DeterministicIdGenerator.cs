using System.Security.Cryptography;
using System.Text;

namespace ImagingPipeline.TbPublisher.Domain;

public static class DeterministicIdGenerator
{
    public static string CreateMissionId(string messageId) => CreateId(messageId, "mission");

    public static string CreateRequestId(string messageId, int tilingIndex) =>
        CreateId($"{messageId}:{tilingIndex}", "request");

    private static string CreateId(string seed, string purpose) =>
        CreateDeterministicGuid($"{seed}:{purpose}").ToString();

    private static Guid CreateDeterministicGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, guidBytes.Length);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
