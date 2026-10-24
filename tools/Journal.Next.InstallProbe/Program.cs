using System.Security.Cryptography;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Access;

if (args is not [var executable, var database]) throw new ArgumentException("Expected installed AccessHost and synthetic MDB.");
var before = SHA256.HashData(await File.ReadAllBytesAsync(database));
var host = new AccessHostClient(database, executable);
var machines = await host.ReadMachinesAsync();
var price = await host.ReadAsync(PriceTables.Laser);
if (price.Rates.Count == 0) throw new InvalidDataException("No prices returned by installed x86 host.");
var after = SHA256.HashData(await File.ReadAllBytesAsync(database));
if (!CryptographicOperations.FixedTimeEquals(before, after)) throw new InvalidDataException("Source MDB changed.");
Console.WriteLine($"Installed x86 host read {machines.Count} machines and {price.Rates.Count} price columns; MDB bytes unchanged.");
