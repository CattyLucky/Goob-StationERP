// SPDX-FileCopyrightText: 2023 Chief-Engineer <119664036+Chief-Engineer@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 DrSmugleaf <DrSmugleaf@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 Leon Friedrich <60421075+ElectroJr@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 MishaUnity <81403616+MishaUnity@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 Pieter-Jan Briers <pieterjan.briers@gmail.com>
// SPDX-FileCopyrightText: 2023 PrPleGoo <PrPleGoo@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 Simon <63975668+Simyon264@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 0x6273 <0x40@keemail.me>
// SPDX-FileCopyrightText: 2024 AsnDen <75905158+AsnDen@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Fildrance <fildrance@gmail.com>
// SPDX-FileCopyrightText: 2024 Julian Giebel <juliangiebel@live.de>
// SPDX-FileCopyrightText: 2024 Mervill <mervills.email@gmail.com>
// SPDX-FileCopyrightText: 2024 Pieter-Jan Briers <pieterjan.briers+git@gmail.com>
// SPDX-FileCopyrightText: 2024 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2024 Red Mushie <82113471+redmushie@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Tayrtahn <tayrtahn@gmail.com>
// SPDX-FileCopyrightText: 2024 metalgearsloth <31366439+metalgearsloth@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 nikthechampiongr <32041239+nikthechampiongr@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 pa.pecherskij <pa.pecherskij@interfax.ru>
// SPDX-FileCopyrightText: 2024 themias <89101928+themias@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using Content.Server.Administration.Logs;
using Content.Server.CartridgeLoader.Cartridges;
using Content.Server.CartridgeLoader;
using Content.Server.Chat.Managers;
using Content.Server.Discord;
using Content.Server.GameTicking;
using Content.Server.MassMedia.Components;
using Content.Server.Popups;
using Content.Server.Station.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.CartridgeLoader;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.MassMedia.Components;
using Content.Shared.MassMedia.Systems;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Server;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.MassMedia.Systems;

public sealed class NewsSystem : SharedNewsSystem
{
    [Dependency] private readonly AccessReaderSystem _accessReaderSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoaderSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly DiscordWebhook _discord = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IBaseServer _baseServer = default!;

    private WebhookIdentifier? _webhookId = null;
    private Color _webhookEmbedColor;
    private bool _webhookSendDuringRound;

    public override void Initialize()
    {
        base.Initialize();

        // Discord hook
        _cfg.OnValueChanged(CCVars.DiscordNewsWebhook,
            value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                    _discord.GetWebhook(value, data => _webhookId = data.ToIdentifier());
            }, true);

        _cfg.OnValueChanged(CCVars.DiscordNewsWebhookEmbedColor, value =>
            {
                _webhookEmbedColor = Color.LawnGreen;
                if (Color.TryParse(value, out var color))
                    _webhookEmbedColor = color;
            }, true);

        _cfg.OnValueChanged(CCVars.DiscordNewsWebhookSendDuringRound, value => _webhookSendDuringRound = value, true);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEndMessageEvent);

        // News writer
        SubscribeLocalEvent<NewsWriterComponent, MapInitEvent>(OnMapInit);

        // New writer bui messages
        Subs.BuiEvents<NewsWriterComponent>(NewsWriterUiKey.Key, subs =>
        {
            subs.Event<NewsWriterDeleteMessage>(OnWriteUiDeleteMessage);
            subs.Event<NewsWriterArticlesRequestMessage>(OnRequestArticlesUiMessage);
            subs.Event<NewsWriterPublishMessage>(OnWriteUiPublishMessage);
            subs.Event<NewsWriterSaveDraftMessage>(OnNewsWriterDraftUpdatedMessage);
            subs.Event<NewsWriterRequestDraftMessage>(OnRequestArticleDraftMessage);
        });

        // News reader
        SubscribeLocalEvent<NewsReaderCartridgeComponent, NewsArticlePublishedEvent>(OnArticlePublished);
        SubscribeLocalEvent<NewsReaderCartridgeComponent, NewsArticleDeletedEvent>(OnArticleDeleted);
        SubscribeLocalEvent<NewsReaderCartridgeComponent, CartridgeMessageEvent>(OnReaderUiMessage);
        SubscribeLocalEvent<NewsReaderCartridgeComponent, CartridgeUiReadyEvent>(OnReaderUiReady);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<NewsWriterComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.PublishEnabled || _timing.CurTime < comp.NextPublish)
                continue;

            comp.PublishEnabled = true;
            UpdateWriterUi((uid, comp));
        }
    }

    #region Writer Event Handlers

    private void OnMapInit(Entity<NewsWriterComponent> ent, ref MapInitEvent args)
    {
        var station = _station.GetOwningStation(ent);
        if (!station.HasValue)
            return;

        EnsureComp<StationNewsComponent>(station.Value);
    }

    private void OnWriteUiDeleteMessage(Entity<NewsWriterComponent> ent, ref NewsWriterDeleteMessage msg)
    {
        if (!TryGetArticles(ent, out var articles))
            return;

        if (msg.ArticleNum >= articles.Count)
            return;

        var article = articles[msg.ArticleNum];
        if (CanUse(msg.Actor, ent.Owner))
        {
            _adminLogger.Add(
                LogType.Chat, LogImpact.Medium,
                $"{ToPrettyString(msg.Actor):actor} deleted news article {article.Title} by {article.Author}: {article.Content}"
                );

            articles.RemoveAt(msg.ArticleNum);
            _audio.PlayPvs(ent.Comp.ConfirmSound, ent);
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("news-write-no-access-popup"), ent, PopupType.SmallCaution);
            _audio.PlayPvs(ent.Comp.NoAccessSound, ent);
        }

        var args = new NewsArticleDeletedEvent();
        var query = EntityQueryEnumerator<NewsReaderCartridgeComponent>();
        while (query.MoveNext(out var readerUid, out _))
        {
            RaiseLocalEvent(readerUid, ref args);
        }

        UpdateWriterDevices();
    }

    private void OnRequestArticlesUiMessage(Entity<NewsWriterComponent> ent, ref NewsWriterArticlesRequestMessage msg)
    {
        UpdateWriterUi(ent);
    }

    private void OnWriteUiPublishMessage(Entity<NewsWriterComponent> ent, ref NewsWriterPublishMessage msg)
    {
        if (!ent.Comp.PublishEnabled)
            return;

        if (!TryGetArticles(ent, out var articles))
            return;

        if (!CanUse(msg.Actor, ent.Owner))
            return;

        ent.Comp.PublishEnabled = false;
        ent.Comp.NextPublish = _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.PublishCooldown);

        var tryGetIdentityShortInfoEvent = new TryGetIdentityShortInfoEvent(ent, msg.Actor);
        RaiseLocalEvent(tryGetIdentityShortInfoEvent);
        string? authorName = tryGetIdentityShortInfoEvent.Title;

        var title = msg.Title.Trim();
        var content = msg.Content.Trim();

        var article = new NewsArticle
        {
            Title = title.Length <= MaxTitleLength ? title : $"{title[..MaxTitleLength]}...",
            Content = content.Length <= MaxContentLength ? content : $"{content[..MaxContentLength]}...",
            Author = authorName,
            ShareTime = _ticker.RoundDuration()
        };

        _audio.PlayPvs(ent.Comp.ConfirmSound, ent);

        _adminLogger.Add(
            LogType.Chat,
            LogImpact.Medium,
            $"{ToPrettyString(msg.Actor):actor} created news article {article.Title} by {article.Author}: {article.Content}"
            );

        _chatManager.SendAdminAnnouncement(Loc.GetString("news-publish-admin-announcement",
            ("actor", msg.Actor),
            ("title", article.Title),
            ("author", article.Author ?? Loc.GetString("news-read-ui-no-author"))
            ));

        articles.Add(article);

        var args = new NewsArticlePublishedEvent(article);
        var query = EntityQueryEnumerator<NewsReaderCartridgeComponent>();
        while (query.MoveNext(out var readerUid, out _))
        {
            RaiseLocalEvent(readerUid, ref args);
        }

        if (_webhookSendDuringRound)
            Task.Run(async () => await SendArticleToDiscordWebhook(article));

        UpdateWriterDevices();
    }
    #endregion

    #region Reader Event Handlers

    private void OnArticlePublished(Entity<NewsReaderCartridgeComponent> ent, ref NewsArticlePublishedEvent args)
    {
        if (Comp<CartridgeComponent>(ent).LoaderUid is not { } loaderUid)
            return;

        UpdateReaderUi(ent, loaderUid);

        if (!ent.Comp.NotificationOn)
            return;

        _cartridgeLoaderSystem.SendNotification(
            loaderUid,
            Loc.GetString("news-pda-notification-header"),
            args.Article.Title);
    }

    private void OnArticleDeleted(Entity<NewsReaderCartridgeComponent> ent, ref NewsArticleDeletedEvent args)
    {
        if (Comp<CartridgeComponent>(ent).LoaderUid is not { } loaderUid)
            return;

        UpdateReaderUi(ent, loaderUid);
    }

    private void OnReaderUiMessage(Entity<NewsReaderCartridgeComponent> ent, ref CartridgeMessageEvent args)
    {
        if (args is not NewsReaderUiMessageEvent message)
            return;

        switch (message.Action)
        {
            case NewsReaderUiAction.Next:
                NewsReaderLeafArticle(ent, 1);
                break;
            case NewsReaderUiAction.Prev:
                NewsReaderLeafArticle(ent, -1);
                break;
            case NewsReaderUiAction.NotificationSwitch:
                ent.Comp.NotificationOn = !ent.Comp.NotificationOn;
                break;
        }

        UpdateReaderUi(ent, GetEntity(args.LoaderUid));
    }

    private void OnReaderUiReady(Entity<NewsReaderCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateReaderUi(ent, args.Loader);
    }
    #endregion

    private bool TryGetArticles(EntityUid uid, [NotNullWhen(true)] out List<NewsArticle>? articles)
    {
        if (_station.GetOwningStation(uid) is not { } station ||
            !TryComp<StationNewsComponent>(station, out var stationNews))
        {
            articles = null;
            return false;
        }

        articles = stationNews.Articles;
        return true;
    }

    private void UpdateWriterUi(Entity<NewsWriterComponent> ent)
    {
        if (!_ui.HasUi(ent, NewsWriterUiKey.Key))
            return;

        if (!TryGetArticles(ent, out var articles))
            return;

        var state = new NewsWriterBoundUserInterfaceState(articles.ToArray(), ent.Comp.PublishEnabled, ent.Comp.NextPublish, ent.Comp.DraftTitle, ent.Comp.DraftContent);
        _ui.SetUiState(ent.Owner, NewsWriterUiKey.Key, state);
    }

    private void UpdateReaderUi(Entity<NewsReaderCartridgeComponent> ent, EntityUid loaderUid)
    {
        if (!TryGetArticles(ent, out var articles))
            return;

        NewsReaderLeafArticle(ent, 0);

        if (articles.Count == 0)
        {
            _cartridgeLoaderSystem.UpdateCartridgeUiState(loaderUid, new NewsReaderEmptyBoundUserInterfaceState(ent.Comp.NotificationOn));
            return;
        }

        var state = new NewsReaderBoundUserInterfaceState(
            articles[ent.Comp.ArticleNumber],
            ent.Comp.ArticleNumber + 1,
            articles.Count,
            ent.Comp.NotificationOn);

        _cartridgeLoaderSystem.UpdateCartridgeUiState(loaderUid, state);
    }

    private void NewsReaderLeafArticle(Entity<NewsReaderCartridgeComponent> ent, int leafDir)
    {
        if (!TryGetArticles(ent, out var articles))
            return;

        ent.Comp.ArticleNumber += leafDir;

        if (ent.Comp.ArticleNumber >= articles.Count)
            ent.Comp.ArticleNumber = 0;

        if (ent.Comp.ArticleNumber < 0)
            ent.Comp.ArticleNumber = articles.Count - 1;
    }

    private void UpdateWriterDevices()
    {
        var query = EntityQueryEnumerator<NewsWriterComponent>();
        while (query.MoveNext(out var owner, out var comp))
        {
            UpdateWriterUi((owner, comp));
        }
    }

    private bool CanUse(EntityUid user, EntityUid console)
    {

        if (TryComp<AccessReaderComponent>(console, out var accessReaderComponent))
        {
            return _accessReaderSystem.IsAllowed(user, console, accessReaderComponent);
        }
        return true;
    }

    private void OnNewsWriterDraftUpdatedMessage(Entity<NewsWriterComponent> ent, ref NewsWriterSaveDraftMessage args)
    {
        ent.Comp.DraftTitle = args.DraftTitle;
        ent.Comp.DraftContent = args.DraftContent;
    }

    private void OnRequestArticleDraftMessage(Entity<NewsWriterComponent> ent, ref NewsWriterRequestDraftMessage msg)
    {
        UpdateWriterUi(ent);
    }

    #region Discord Hook

    private void OnRoundEndMessageEvent(RoundEndMessageEvent ev)
    {
        if (_webhookSendDuringRound)
            return;

        var query = EntityManager.EntityQueryEnumerator<StationNewsComponent>();

        while (query.MoveNext(out _, out var comp))
        {
            SendArticlesListToDiscordWebhook(comp.Articles.OrderBy(article => article.ShareTime));
        }
    }

    private async void SendArticlesListToDiscordWebhook(IOrderedEnumerable<NewsArticle> articles)
    {
        foreach (var article in articles)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)); // TODO: proper discord rate limit handling
            await SendArticleToDiscordWebhook(article);
        }
    }

    private async Task SendArticleToDiscordWebhook(NewsArticle article)
    {
        if (_webhookId is null)
            return;

        try
        {
            var embed = new WebhookEmbed
            {
                Title = article.Title,
                // There is no need to cut article content. It's MaxContentLength smaller then discord's limit (4096):
                Description = FormattedMessage.RemoveMarkupPermissive(article.Content),
                Color = _webhookEmbedColor.ToArgb() & 0xFFFFFF, // HACK: way to get hex without A (transparency)
                Footer = new WebhookEmbedFooter
                {
                    Text = Loc.GetString("news-discord-footer",
                        ("server", _baseServer.ServerName),
                        ("round", _ticker.RoundId),
                        ("author", article.Author ?? Loc.GetString("news-discord-unknown-author")),
                        ("time", article.ShareTime.ToString(@"hh\:mm\:ss")))
                }
            };
            var payload = new WebhookPayload { Embeds = [embed] };
            await _discord.CreateMessage(_webhookId.Value, payload);
            Log.Info("Sent news article to Discord webhook");
        }
        catch (Exception e)
        {
            Log.Error($"Error while sending discord news article:\n{e}");
        }
    }

    #endregion
}
