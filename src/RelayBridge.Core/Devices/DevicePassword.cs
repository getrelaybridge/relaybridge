// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text;

namespace RelayBridge.Core.Devices;

public static class DevicePassword
{
    private const int Iterations = 600_000;
    private const int MaximumAcceptedIterations = 10_000_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const string Prefix = "v1$pbkdf2-sha256";

    public static GeneratedDevicePassword Generate()
    {
        var secretBytes = RandomNumberGenerator.GetBytes(24);
        try
        {
            var plaintext = Convert.ToBase64String(secretBytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return new GeneratedDevicePassword(plaintext, CreateVerifier(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public static string CreateVerifier(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var passwordBytes = Encoding.UTF8.GetBytes(plaintext);
        var hash = new byte[HashLength];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, hash, Iterations, HashAlgorithmName.SHA256);
            return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool Verify(string plaintext, string verifier)
    {
        if (string.IsNullOrEmpty(plaintext) || string.IsNullOrEmpty(verifier))
        {
            return false;
        }

        var parts = verifier.Split('$', StringSplitOptions.None);
        if (parts.Length != 5 ||
            !string.Equals($"{parts[0]}${parts[1]}", Prefix, StringComparison.Ordinal) ||
            !int.TryParse(parts[2], out var iterations) ||
            iterations < Iterations ||
            iterations > MaximumAcceptedIterations)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length < SaltLength || expected.Length != HashLength)
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            return false;
        }

        var passwordBytes = Encoding.UTF8.GetBytes(plaintext);
        var actual = new byte[expected.Length];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, actual, iterations, HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
        }
    }
}

public sealed class GeneratedDevicePassword(string plaintext, string verifier)
{
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Plaintext { get; } = plaintext;

    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Verifier { get; } = verifier;

    public override string ToString() => $"{nameof(GeneratedDevicePassword)} {{ Plaintext = [REDACTED], Verifier = [REDACTED] }}";
}
