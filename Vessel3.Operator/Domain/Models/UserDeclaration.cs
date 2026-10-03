namespace Vessel3.Operator.Domain.Models;

public sealed record UserDeclaration(
    ResourceIdentity Identity,
    ResourceIdentity ServerReference,
    string Username,
    string Role = "Member",
    SecretOutputDefinition? SecretOutput = null);
