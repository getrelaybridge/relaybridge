// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Mail;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace RelayBridge.Core.Devices;

public sealed record DeviceDefinition
{
    private DeviceDefinition(
        Guid id,
        string name,
        string? description,
        bool enabled,
        DeviceAuthenticationMode authenticationMode,
        string? smtpUsername,
        string? passwordVerifier,
        IReadOnlyList<string> allowedNetworks,
        IReadOnlyList<string> allowedSenders,
        DateTimeOffset createdUtc,
        long revision)
    {
        Id = id;
        Name = name;
        Description = description;
        Enabled = enabled;
        AuthenticationMode = authenticationMode;
        SmtpUsername = smtpUsername;
        PasswordVerifier = passwordVerifier;
        AllowedNetworks = allowedNetworks;
        AllowedSenders = allowedSenders;
        CreatedUtc = createdUtc;
        Revision = revision;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string? Description { get; }

    public bool Enabled { get; }

    public DeviceAuthenticationMode AuthenticationMode { get; }

    public string? SmtpUsername { get; }

    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string? PasswordVerifier { get; }

    public IReadOnlyList<string> AllowedNetworks { get; }

    public IReadOnlyList<string> AllowedSenders { get; }

    public DateTimeOffset CreatedUtc { get; }

    public long Revision { get; }

    public override string ToString()
    {
        return $"{nameof(DeviceDefinition)} {{ Id = {Id:D}, Enabled = {Enabled}, AuthenticationMode = {AuthenticationMode}, Revision = {Revision}, PasswordVerifier = [REDACTED] }}";
    }

    public static DeviceDefinition CreateAuthenticated(
        Guid id,
        string name,
        bool enabled,
        string smtpUsername,
        string passwordVerifier,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        DateTimeOffset createdUtc,
        long revision = 0)
    {
        return CreateAuthenticated(
            id,
            name,
            description: null,
            enabled,
            smtpUsername,
            passwordVerifier,
            allowedNetworks,
            allowedSenders,
            createdUtc,
            revision);
    }

    public static DeviceDefinition CreateAuthenticated(
        Guid id,
        string name,
        string? description,
        bool enabled,
        string smtpUsername,
        string passwordVerifier,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        DateTimeOffset createdUtc,
        long revision = 0)
    {
        if (string.IsNullOrWhiteSpace(smtpUsername))
        {
            throw new ArgumentException("An authenticated device requires an SMTP username.", nameof(smtpUsername));
        }

        if (string.IsNullOrWhiteSpace(passwordVerifier))
        {
            throw new ArgumentException("An authenticated device requires a password verifier.", nameof(passwordVerifier));
        }

        return Create(
            id,
            name,
            description,
            enabled,
            DeviceAuthenticationMode.Authenticated,
            smtpUsername.Trim(),
            passwordVerifier,
            allowedNetworks,
            allowedSenders,
            createdUtc,
            revision);
    }

    public static DeviceDefinition CreateLegacy(
        Guid id,
        string name,
        bool enabled,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        DateTimeOffset createdUtc,
        long revision = 0)
    {
        return CreateLegacy(
            id,
            name,
            description: null,
            enabled,
            allowedNetworks,
            allowedSenders,
            createdUtc,
            revision);
    }

    public static DeviceDefinition CreateLegacy(
        Guid id,
        string name,
        string? description,
        bool enabled,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        DateTimeOffset createdUtc,
        long revision = 0)
    {
        return Create(
            id,
            name,
            description,
            enabled,
            DeviceAuthenticationMode.Legacy,
            null,
            null,
            allowedNetworks,
            allowedSenders,
            createdUtc,
            revision);
    }

    public bool IsSourceAllowed(IPAddress sourceAddress)
    {
        return AllowedNetworks.Any(value => IpNetwork.Parse(value).Contains(sourceAddress));
    }

    public bool IsSenderAllowed(string sender)
    {
        return TryNormalizeSender(sender, out var normalized) &&
            AllowedSenders.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public DeviceDefinition WithConfiguration(
        string name,
        string? description,
        bool enabled,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders)
    {
        return AuthenticationMode == DeviceAuthenticationMode.Authenticated
            ? CreateAuthenticated(
                Id,
                name,
                description,
                enabled,
                SmtpUsername!,
                PasswordVerifier!,
                allowedNetworks,
                allowedSenders,
                CreatedUtc,
                Revision)
            : CreateLegacy(
                Id,
                name,
                description,
                enabled,
                allowedNetworks,
                allowedSenders,
                CreatedUtc,
                Revision);
    }

    public DeviceDefinition WithPasswordVerifier(string passwordVerifier)
    {
        if (AuthenticationMode != DeviceAuthenticationMode.Authenticated)
        {
            throw new InvalidOperationException("Legacy devices do not have an SMTP password.");
        }

        return CreateAuthenticated(
            Id,
            Name,
            Description,
            Enabled,
            SmtpUsername!,
            passwordVerifier,
            AllowedNetworks,
            AllowedSenders,
            CreatedUtc,
            Revision);
    }

    public static bool TryNormalizeSender(string sender, out string normalized)
    {
        normalized = string.Empty;
        if (!MailAddress.TryCreate(sender, out var address) ||
            !string.Equals(address.Address, sender, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = address.Address.ToLowerInvariant();
        return true;
    }

    private static DeviceDefinition Create(
        Guid id,
        string name,
        string? description,
        bool enabled,
        DeviceAuthenticationMode authenticationMode,
        string? smtpUsername,
        string? passwordVerifier,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        DateTimeOffset createdUtc,
        long revision)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A device ID is required.", nameof(id));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A device name is required.", nameof(name));
        }

        if (name.Trim().Length > 100)
        {
            throw new ArgumentException("A device name cannot exceed 100 characters.", nameof(name));
        }

        if (description?.Trim().Length > 500)
        {
            throw new ArgumentException("A device description cannot exceed 500 characters.", nameof(description));
        }

        var networks = allowedNetworks
            .Select(IpNetwork.Parse)
            .ToArray();
        if (networks.Length == 0)
        {
            throw new ArgumentException("Every device requires at least one source IP or CIDR restriction.", nameof(allowedNetworks));
        }

        if (networks.Any(network => network.PrefixLength == 0))
        {
            throw new ArgumentException("Catch-all source networks are not allowed.", nameof(allowedNetworks));
        }

        if (authenticationMode == DeviceAuthenticationMode.Legacy && networks.Any(network => !network.IsPrivateOrLocal))
        {
            throw new ArgumentException("Legacy devices must be restricted to private or local networks.", nameof(allowedNetworks));
        }

        var normalizedNetworks = networks
            .Select(network => network.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var senders = allowedSenders
            .Select(sender => TryNormalizeSender(sender, out var normalized)
                ? normalized
                : throw new ArgumentException($"'{sender}' is not a valid sender address.", nameof(allowedSenders)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (senders.Length == 0)
        {
            throw new ArgumentException("Every device requires at least one allowed sender.", nameof(allowedSenders));
        }

        return new DeviceDefinition(
            id,
            name.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            enabled,
            authenticationMode,
            smtpUsername,
            passwordVerifier,
            normalizedNetworks,
            senders,
            createdUtc.ToUniversalTime(),
            revision);
    }
}
