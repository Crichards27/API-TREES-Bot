using System.Text.Json.Serialization;

namespace ApiTrees.Core;

public sealed record TeamAvailability(string Name, bool Available);
public sealed record TeamList(int GameId, string Status, List<TeamAvailability> Teams);
public sealed record TeamSelection(string Team, string Token, string Message);
public sealed record Identity(string Team, bool Authenticated);
public sealed record Inventory(int Money, int Fruit, int Seeds, int Poison, int Molotov);
public sealed record Plot(int Id, int Row, int Column, int? TreeId, List<int> ConnectedPlots);
public sealed record PlotList(List<Plot> Plots);
public sealed record Branch(int Id, string Status, int Fruit);
public sealed record Tree(int Id, int PlotId, string Status, int BranchCount, int Fruit, List<Branch> Branches);
public sealed record TreeList(List<Tree> Trees);
public sealed record PublicTeam(string Team, int Money, int Plots, int Trees, List<int> PlotIds);
public sealed record MarketItem(string Item, int Price);
public sealed record Market(List<MarketItem> Items);
public sealed record Purchase(string Item, int Quantity, int UnitPrice, int TotalCost, int MoneyRemaining,
    int Poison, int Molotov, List<int> PlotIds, string Message);
public sealed record Harvest(int BranchId, int Harvested, int BranchFruitRemaining, int InventoryFruit);
public sealed record Standing(int Place, string Team, int Money, int Fruit, int Trees, int Score);
public sealed record Results(int GameId, List<string> Winners, bool Tie, List<Standing> Standings);
public sealed record Session(string Server, string Team, string Token, int GameId);
public sealed record SelectRequest(string Team);
public sealed record AmountRequest(int Amount);
public sealed record BuyRequest(string Item, int Quantity);
public sealed record ConvertRequest(int Amount, string ConvertTo);
public sealed record AttackRequest(int PlotId);

// Generated metadata also works in trimmed MAUI builds without reflection serialization.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TeamList))]
[JsonSerializable(typeof(TeamSelection))]
[JsonSerializable(typeof(Identity))]
[JsonSerializable(typeof(Inventory))]
[JsonSerializable(typeof(PlotList))]
[JsonSerializable(typeof(TreeList))]
[JsonSerializable(typeof(PublicTeam))]
[JsonSerializable(typeof(Market))]
[JsonSerializable(typeof(Purchase))]
[JsonSerializable(typeof(Harvest))]
[JsonSerializable(typeof(Results))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(SelectRequest))]
[JsonSerializable(typeof(AmountRequest))]
[JsonSerializable(typeof(BuyRequest))]
[JsonSerializable(typeof(ConvertRequest))]
[JsonSerializable(typeof(AttackRequest))]
public partial class GameJson : JsonSerializerContext;
