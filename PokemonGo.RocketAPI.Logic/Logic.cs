﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AllEnum;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.Extensions;
using PokemonGo.RocketAPI.GeneratedCode;
using PokemonGo.RocketAPI.Logic.Utils;

namespace PokemonGo.RocketAPI.Logic
{
    public class Logic
    {
        private readonly Client _client;
        private readonly ISettings _clientSettings;
        private readonly Inventory _inventory; 

        public Logic(ISettings clientSettings)
        {
            _clientSettings = clientSettings;
            _client = new Client(_clientSettings);
            _inventory = new Inventory(_client);
        }

        public async void Execute()
        {
            Logger.ColoredConsoleWrite(ConsoleColor.Green, $"Starting Execute on login server: {_clientSettings.AuthType}", LogLevel.Info);

            if (_clientSettings.AuthType == AuthType.Ptc)
                await _client.DoPtcLogin(_clientSettings.PtcUsername, _clientSettings.PtcPassword);
            else if (_clientSettings.AuthType == AuthType.Google)
                await _client.DoGoogleLogin();

            while (true)
            {
                try
                {
                    await _client.SetServer();
                    await StatsLog(_client);
                    await TransferDuplicatePokemon(true);
                    await RecycleItems();
                    await RepeatAction(10, async () => await ExecuteFarmingPokestopsAndPokemons(_client));

                    /*
                * Example calls below
                *
                var profile = await _client.GetProfile();
                var settings = await _client.GetSettings();
                var mapObjects = await _client.GetMapObjects();
                var inventory = await _client.GetInventory();
                var pokemons = inventory.InventoryDelta.InventoryItems.Select(i => i.InventoryItemData?.Pokemon).Where(p => p != null && p?.PokemonId > 0);
                */
                }
                catch (Exception ex)
                {
                    Logger.ColoredConsoleWrite(ConsoleColor.DarkRed, $"Exception: {ex}", LogLevel.Error);
                }
                Logger.ColoredConsoleWrite(ConsoleColor.Green, "Starting again.. But waiting 10 Seconds..");
                await Task.Delay(10000);
            }
        }

        public async Task RepeatAction(int repeat, Func<Task> action)
        {
            for (int i = 0; i < repeat; i++)
                await action();
        }

        private static async Task StatsLog(Client client)
        {
            var inventory = await client.GetInventory();
            var stats = inventory.InventoryDelta.InventoryItems.Select(i => i.InventoryItemData.PlayerStats).ToArray();
            foreach (var c in stats)
            {
                if (c != null)
                {
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "_____________________________");
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "Level: " + c.Level);
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "EXP Needed: " + (c.NextLevelXp - c.PrevLevelXp));
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "Current EXP: " + (c.Experience - c.PrevLevelXp));
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "EXP to Level up: " + ((c.NextLevelXp) - (c.Experience)));
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "KM Walked: " + c.KmWalked);
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "PokeStops visited: " + c.PokeStopVisits);
                    Logger.ColoredConsoleWrite(ConsoleColor.Cyan, "_____________________________");
                }
            }
        }


        private int count = 0;

        private async Task ExecuteFarmingPokestopsAndPokemons(Client client)
        {

            Resources.OutPutWalking = true;
            var mapObjects = await client.GetMapObjects();

            var pokeStops = mapObjects.MapCells.SelectMany(i => i.Forts).Where(i => i.Type == FortType.Checkpoint && i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime());

            foreach (var pokeStop in pokeStops)
            {

                count++;
                if (count == 3)
                {
                    count = 0;
                    StatsLog(client);
                }

                var update = await client.UpdatePlayerLocation(pokeStop.Latitude, pokeStop.Longitude);
                //var fortInfo = await client.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                var fortSearch = await client.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);

                Logger.ColoredConsoleWrite(ConsoleColor.Green, $"Farmed XP: {fortSearch.ExperienceAwarded}, Gems: { fortSearch.GemsAwarded}, Eggs: {fortSearch.PokemonDataEgg} Items: {StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch.ItemsAwarded)}", LogLevel.Info);

                await Task.Delay(1000);
                Resources.OutPutWalking = true;
                await ExecuteCatchAllNearbyPokemons(client);
            }
        }

        private async Task ExecuteCatchAllNearbyPokemons(Client client)
        {
            var mapObjects = await client.GetMapObjects();

            var pokemons = mapObjects.MapCells.SelectMany(i => i.CatchablePokemons);

            foreach (var pokemon in pokemons)
            {
                count++;
                if (count == 3)
                {
                    count = 0;
                    StatsLog(client);
                }
                Resources.OutPutWalking = true;
                await client.UpdatePlayerLocation(pokemon.Latitude, pokemon.Longitude);
                var encounterPokemonResponse = await client.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnpointId);
                var pokemonCP = encounterPokemonResponse?.WildPokemon?.PokemonData?.Cp;
                var pokeball = await GetBestBall(pokemonCP);

                CatchPokemonResponse caughtPokemonResponse;
                do
                {
                    caughtPokemonResponse = await client.CatchPokemon(pokemon.EncounterId, pokemon.SpawnpointId, pokemon.Latitude, pokemon.Longitude, pokeball);
                }
                while (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchMissed);
                
                if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                {
                    Logger.ColoredConsoleWrite(ConsoleColor.Green, $"We caught a {pokemon.PokemonId} with CP {encounterPokemonResponse?.WildPokemon?.PokemonData?.Cp} using a {pokeball}");
                } else
                {
                    Logger.ColoredConsoleWrite(ConsoleColor.DarkYellow, $"{pokemon.PokemonId} with CP {encounterPokemonResponse?.WildPokemon?.PokemonData?.Cp} got away while using a {pokeball}..");
                }
                await Task.Delay(3000);
                Resources.OutPutWalking = true;
            }
        }

        private async Task EvolveAllGivenPokemons(IEnumerable<Pokemon> pokemonToEvolve)
        {
            foreach (var pokemon in pokemonToEvolve)
            {
                EvolvePokemonOut evolvePokemonOutProto;
                do
                {
                    evolvePokemonOutProto = await _client.EvolvePokemon((ulong)pokemon.Id);

                    if (evolvePokemonOutProto.Result == EvolvePokemonOut.Types.EvolvePokemonStatus.PokemonEvolvedSuccess)
                        Logger.ColoredConsoleWrite(ConsoleColor.Blue, $"Evolved {pokemon.PokemonType} successfully for {evolvePokemonOutProto.ExpAwarded}xp", LogLevel.Info);
                    else
                        Logger.ColoredConsoleWrite(ConsoleColor.Blue, $"Failed to evolve {pokemon.PokemonType}. EvolvePokemonOutProto.Result was {evolvePokemonOutProto.Result}, stopping evolving {pokemon.PokemonType}", LogLevel.Info);

                    await Task.Delay(3000);
                }
                while (evolvePokemonOutProto.Result == EvolvePokemonOut.Types.EvolvePokemonStatus.PokemonEvolvedSuccess);

                await Task.Delay(3000);
            }
        }

        private async Task TransferDuplicatePokemon(bool keepPokemonsThatCanEvolve = false)
        {
            if (_clientSettings.TransferDoublePokemons)
            {
                var duplicatePokemons = await _inventory.GetDuplicatePokemonToTransfer();

                foreach (var duplicatePokemon in duplicatePokemons)
                {
                    if (!_clientSettings.pokemonsToHold.Contains(duplicatePokemon.PokemonId))
                    {
                        var transfer = await _client.TransferPokemon(duplicatePokemon.Id);
                        Logger.ColoredConsoleWrite(ConsoleColor.Yellow, $"Transfer {duplicatePokemon.PokemonId} with {duplicatePokemon.Cp} CP", LogLevel.Info);
                        await Task.Delay(500);
                    }
                }
            }
        }

        private async Task RecycleItems()
        {
            var items = await _inventory.GetItemsToRecycle(_clientSettings);

            foreach (var item in items)
            {
                var transfer = await _client.RecycleItem((AllEnum.ItemId)item.Item_, item.Count);
                Logger.ColoredConsoleWrite(ConsoleColor.Yellow, $"Recycled {item.Count}x {(AllEnum.ItemId)item.Item_}", LogLevel.Info);
                await Task.Delay(500);
            }
        }

        private async Task<MiscEnums.Item> GetBestBall(int? pokemonCp)
        {
            var pokeBallsCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_POKE_BALL);
            var greatBallsCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_GREAT_BALL);
            var ultraBallsCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_ULTRA_BALL);
            var masterBallsCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_MASTER_BALL);

            if (masterBallsCount > 0 && pokemonCp >= 1000)
                return MiscEnums.Item.ITEM_MASTER_BALL;
            else if (ultraBallsCount > 0 && pokemonCp >= 1000)
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            else if (greatBallsCount > 0 && pokemonCp >= 1000)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            if (ultraBallsCount > 0 && pokemonCp >= 600)
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            else if (greatBallsCount > 0 && pokemonCp >= 600)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            if (greatBallsCount > 0 && pokemonCp >= 350)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            if (pokeBallsCount > 0)
                return MiscEnums.Item.ITEM_POKE_BALL;
            if (greatBallsCount > 0)
                return MiscEnums.Item.ITEM_GREAT_BALL;
            if (ultraBallsCount > 0)
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            if (masterBallsCount > 0)
                return MiscEnums.Item.ITEM_MASTER_BALL;

            return MiscEnums.Item.ITEM_POKE_BALL;
        }
    }
}