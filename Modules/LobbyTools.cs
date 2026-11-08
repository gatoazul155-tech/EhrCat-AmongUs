using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EHR.Patches;
using UnityEngine;

namespace EHR.Modules;

// Ferramentas do lobby: /colorname (cor do nome, so no lobby) e /afk (espectador ate mexer ou falar).
public static class LobbyTools
{
    public static OptionItem NameColorWho;

    public static readonly Dictionary<byte, string> LobbyNameColors = [];
    public static readonly HashSet<byte> AfkPlayers = [];
    public static readonly Dictionary<byte, string> BaseNames = [];
    public static readonly Dictionary<byte, string> Suffixes = [];
    public static readonly Dictionary<byte, string> SuffixedNames = [];
    private static readonly Dictionary<byte, Vector2> LastPos = [];

    // Cores do Among Us, na ordem da paleta do jogo (Palette.PlayerColors). Aceita nomes em portugues, ingles e sinonimos.
    private static readonly string[][] ColorNames =
    [
        ["vermelho", "red"], // 0
        ["azul", "blue"], // 1
        ["verde", "green"], // 2
        ["rosa", "pink"], // 3
        ["laranja", "orange"], // 4
        ["amarelo", "yellow"], // 5
        ["preto", "black"], // 6
        ["branco", "white"], // 7
        ["roxo", "purple"], // 8
        ["marrom", "marron", "brown"], // 9
        ["ciano", "cyan"], // 10
        ["lima", "verde lima", "lime"], // 11
        ["vinho", "carmim", "carmin", "bordo", "maroon"], // 12
        ["rosa claro", "rose"], // 13
        ["banana"], // 14
        ["cinza", "gray", "grey"], // 15
        ["bege", "caqui", "creme", "tan"], // 16
        ["coral"] // 17
    ];

    private static readonly string[] ColorPrefixes = ["cor de ", "cor ", "color "];

    private static string NormalizeColorText(string input)
    {
        string text = input.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static bool TryParseColor(string input, out int paletteIndex)
    {
        paletteIndex = -1;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string text = NormalizeColorText(input);
        foreach (string prefix in ColorPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length)
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        for (int i = 0; i < ColorNames.Length; i++)
        {
            foreach (string alias in ColorNames[i])
            {
                if (NormalizeColorText(alias) != text) continue;
                paletteIndex = i;
                return true;
            }
        }
        return false;
    }

    // Junta os argumentos a partir de 'from' (permite nomes com espaco, ex.: "cor lime", "rosa claro").
    public static bool TryParseColorArgs(string[] args, int from, out int paletteIndex)
    {
        paletteIndex = -1;
        if (args == null || args.Length <= from) return false;
        return TryParseColor(string.Join(" ", args, from, args.Length - from), out paletteIndex);
    }

    public static bool CanUseNameColor(PlayerControl pc)
    {
        int who = NameColorWho.GetValue(); // 0 Todos, 1 Host, 2 Adms e Mods, 3 VIPs (+ Adms e Mods)
        if (pc.IsHost()) return true;
        return who switch
        {
            0 => true,
            1 => false,
            2 => ChatCommands.IsPlayerModerator(pc.FriendCode),
            _ => ChatCommands.IsPlayerModerator(pc.FriendCode) || ChatCommands.IsPlayerVIP(pc.FriendCode),
        };
    }

    public static bool TryGetLobbyColor(byte playerId, out string hex)
    {
        hex = string.Empty;
        if (!GameStates.IsLobby || !LobbyNameColors.TryGetValue(playerId, out string code)) return false;
        hex = code;
        return true;
    }

    // Sufixos permanentes: arquivo EHR_DATA/Suffixes.txt, linhas "friendcode|sufixo" (igual VIPs/Moderators).
    private static readonly string SuffixFilePath = $"{Main.DataPath}/EHR_DATA/Suffixes.txt";

    private static Dictionary<string, string> ReadSavedSuffixes()
    {
        var result = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(SuffixFilePath)) return result;
            foreach (string line in File.ReadAllLines(SuffixFilePath))
            {
                int sep = line.IndexOf('|');
                if (sep <= 0 || sep >= line.Length - 1) continue;
                result[line[..sep].Trim()] = line[(sep + 1)..].Trim();
            }
        }
        catch (Exception e) { Logger.Error(e.ToString(), "Suffixes"); }
        return result;
    }

    private static void WriteSavedSuffixes(Dictionary<string, string> data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SuffixFilePath));
            File.WriteAllLines(SuffixFilePath, data.Select(kv => $"{kv.Key}|{kv.Value}"));
        }
        catch (Exception e) { Logger.Error(e.ToString(), "Suffixes"); }
    }

    private static Dictionary<string, string> savedCache;
    private static Dictionary<string, string> Saved => savedCache ??= ReadSavedSuffixes();

    public static void SetSavedSuffix(string friendCode, string suffix)
    {
        var data = ReadSavedSuffixes();
        if (string.IsNullOrWhiteSpace(suffix)) data.Remove(friendCode);
        else data[friendCode] = suffix;
        WriteSavedSuffixes(data);
        savedCache = data;
    }

    // Sufixo so no lobby: na lobby garante o sufixo no nome; ao iniciar a partida, volta ao nome original.
    // Sufixo agora fica acima do nome (como VIP), nao dentro do nome salvo: nao altera mais o nome.
    public static bool TryGetSavedSuffix(string friendCode, out string suffix)
    {
        suffix = null;
        return !string.IsNullOrWhiteSpace(friendCode) && Saved.TryGetValue(friendCode, out suffix);
    }

    public static void EnsureLobbySuffix(PlayerControl pc)
    {
        return; // desativado: o sufixo nao entra mais no nome
        if (!GameStates.IsLobby || pc == null || string.IsNullOrWhiteSpace(pc.FriendCode) || !Saved.TryGetValue(pc.FriendCode, out string suffix)) return;
        if (!Main.AllPlayerNames.TryGetValue(pc.PlayerId, out string stored)) return;

        // Ja tem o sufixo (mesmo se o /cn colocou tags de cor por cima): nao adiciona de novo.
        string plain = Regex.Replace(stored, "<color=[^>]*>|</color>", string.Empty);
        if (plain.Contains(suffix, StringComparison.Ordinal)) return;

        BaseNames[pc.PlayerId] = stored;
        string full = $"{suffix} {stored}";
        SuffixedNames[pc.PlayerId] = full;
        Main.AllPlayerNames[pc.PlayerId] = full;
        Utils.DirtyName.Add(pc.PlayerId);
        pc.RpcSetName(full);
    }

    public static void RestoreNamesForGame()
    {
        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
        {
            if (!SuffixedNames.TryGetValue(pc.PlayerId, out string applied) || !BaseNames.TryGetValue(pc.PlayerId, out string original)) continue;
            if (!Main.AllPlayerNames.TryGetValue(pc.PlayerId, out string stored) || stored != applied) continue;
            Main.AllPlayerNames[pc.PlayerId] = original;
            Utils.DirtyName.Add(pc.PlayerId);
            pc.RpcSetName(original);
        }
    }

    // Tira TODAS as copias do sufixo salvo do nome que chegou (o cliente guarda o nome com o sufixo entre partidas).
    public static string StripSavedSuffix(string friendCode, string name)
    {
        if (string.IsNullOrWhiteSpace(friendCode) || string.IsNullOrWhiteSpace(name) || !Saved.TryGetValue(friendCode, out string suffix)) return name;
        string result = name;
        while (result.Contains(suffix, StringComparison.Ordinal)) result = result.Replace(suffix, string.Empty);
        return Regex.Replace(result, @"\s+", " ").Trim();
    }

    // Aplica o sufixo salvo no nome que esta entrando: "SUFIXO nome".
    public static string ApplySavedSuffix(byte playerId, string friendCode, string name)
    {
        return name; // nao mexe mais no nome (o sufixo vai acima do nome, em ApplySuffix)
        if (string.IsNullOrWhiteSpace(friendCode) || !Saved.TryGetValue(friendCode, out string suffix)) return name;
        // Evita "SUFIXO SUFIXO nome" quando o nome ja veio com o sufixo (o cliente reenvia o nome com ele).
        while (name.StartsWith(suffix + " ", StringComparison.Ordinal)) name = name[(suffix.Length + 1)..];
        BaseNames[playerId] = name;
        string full = $"{suffix} {name}";
        SuffixedNames[playerId] = full;
        return full;
    }

    public static void ClearAfk(PlayerControl pc)
    {
        if (!AfkPlayers.Remove(pc.PlayerId)) return;
        ChatCommands.ForcedSpectators.Remove(pc.PlayerId);
        Utils.SendMessage("Você saiu do AFK.", pc.PlayerId);
    }

    // Chamado a cada tick do jogador: se ele se mexeu no lobby, sai do AFK.
    public static void OnPlayerTick(PlayerControl pc)
    {
        if (!GameStates.IsLobby || !AmongUsClient.Instance.AmHost) return;
        EnsureLobbySuffix(pc);
        Vector2 pos = pc.transform.position;
        if (LastPos.TryGetValue(pc.PlayerId, out Vector2 last) && (pos - last).sqrMagnitude > 0.0025f && AfkPlayers.Contains(pc.PlayerId)) ClearAfk(pc);
        LastPos[pc.PlayerId] = pos;
    }
}
