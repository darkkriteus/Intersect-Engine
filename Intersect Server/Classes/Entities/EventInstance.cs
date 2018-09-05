﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Intersect.Enums;
using Intersect.GameObjects;
using Intersect.GameObjects.Conditions;
using Intersect.GameObjects.Crafting;
using Intersect.GameObjects.Events;
using Intersect.Server.Classes.Localization;
using Intersect.Server.Classes.Core;
using Intersect.Server.Classes.Database;
using Intersect.Server.Classes.Database.PlayerData.Characters;
using Intersect.Server.Classes.Events;
using Intersect.Server.Classes.General;

using Intersect.Server.Classes.Maps;
using Intersect.Server.Classes.Networking;
using Intersect.Server.Classes.Spells;
using Intersect.GameObjects.Events.Commands;
using Intersect.Server.Classes.EventProcessing;

namespace Intersect.Server.Classes.Entities
{
    using LegacyDatabase = Intersect.Server.Classes.Core.LegacyDatabase;

    public class EventInstance
    {
        public Guid Id;

        public EventBase BaseEvent;

        public Stack<CommandInstance> CallStack = new Stack<CommandInstance>();
        public int CurrentX;
        public int CurrentY;
        public EventPageInstance[] GlobalPageInstance;
        public bool HoldingPlayer;
        public bool Global;
        public Guid MapId;
        public Client MyClient;
        public Player MyPlayer;
        public bool NpcDeathTriggerd;
        public int PageIndex;
        public EventPageInstance PageInstance;

        //Special conditions
        public bool PlayerHasDied;
        public int SpawnX;
        public int SpawnY;
        public long WaitTimer;

        public bool[] SelfSwitch { get; set; }

        public EventInstance(Guid instanceId, Guid map, Client client, EventBase baseEvent)
        {
            Id = instanceId;
            MyClient = client;
            MapId = map;
            MyPlayer = client.Entity;
            SelfSwitch = new bool[4];
            BaseEvent = baseEvent;
            MapId = map;
            CurrentX = baseEvent.SpawnX;
            CurrentY = baseEvent.SpawnY;
        }

        public EventInstance(Guid instanceId, EventBase baseEvent,Guid map) //Global constructor
        {
            Id = instanceId;
            Global = true;
            MapId = map;
            BaseEvent = baseEvent;
            SelfSwitch = new bool[4];
            GlobalPageInstance = new EventPageInstance[BaseEvent.Pages.Count];
            CurrentX = baseEvent.SpawnX;
            CurrentY = baseEvent.SpawnY;
            for (int i = 0; i < BaseEvent.Pages.Count; i++)
            {
                GlobalPageInstance[i] = new EventPageInstance(BaseEvent, BaseEvent.Pages[i], MapId, this, null);
            }
        }

        public void Update(long timeMs)
        {
            var sendLeave = false;
            if (PageInstance != null)
            {
                //Check for despawn
                if (PageInstance.ShouldDespawn())
                {
                    CurrentX = PageInstance.X;
                    CurrentY = PageInstance.Y;
                    PageInstance = null;
                    PlayerHasDied = false;
                    if (HoldingPlayer)
                    {
                        PacketSender.SendReleasePlayer(MyClient, Id);
                        HoldingPlayer = false;
                    }
                    sendLeave = true;
                }
                else
                {
                    if (!Global)
                        PageInstance.Update(CallStack.Count > 0, timeMs); //Process movement and stuff that is client specific

                    //Check to see if we should process event commands
                    if (CallStack.Count > 0)
                    {
                        var curStack = CallStack.Peek();
                        if (curStack.WaitingForResponse == CommandInstance.EventResponse.Shop && MyPlayer.InShop == null) curStack.WaitingForResponse = CommandInstance.EventResponse.None;
                        if (curStack.WaitingForResponse == CommandInstance.EventResponse.Crafting && MyPlayer.CraftingTableId == Guid.Empty) curStack.WaitingForResponse = CommandInstance.EventResponse.None;
                        if (curStack.WaitingForResponse == CommandInstance.EventResponse.Bank && MyPlayer.InBank == false) curStack.WaitingForResponse = CommandInstance.EventResponse.None;
                        if (curStack.WaitingForResponse == CommandInstance.EventResponse.Quest && !MyPlayer.QuestOffers.Contains(((StartQuestCommand)curStack.WaitingOnCommand).QuestId)) curStack.WaitingForResponse = CommandInstance.EventResponse.None;
                        while (curStack.WaitingForResponse == CommandInstance.EventResponse.None && !PageInstance.ShouldDespawn())
                        {
                            if (curStack.WaitingForRoute != Guid.Empty)
                            {
                                if (curStack.WaitingForRoute == MyPlayer.Id)
                                {
                                    if (MyPlayer.MoveRoute == null ||
                                        (MyPlayer.MoveRoute.Complete &&
                                         MyPlayer.MoveTimer < Globals.System.GetTimeMs()))
                                    {
                                        curStack.WaitingForRoute = Guid.Empty;
                                        curStack.WaitingForRouteMap = Guid.Empty;
                                    }
                                }
                                else
                                {
                                    //Check if the exist exists && if the move route is completed.
                                    foreach (var evt in MyPlayer.EventLookup.Values)
                                    {
                                        if (evt.MapId == curStack.WaitingForRouteMap && evt.BaseEvent.Id == curStack.WaitingForRoute)
                                        {
                                            if (evt.PageInstance == null) break;
                                            if (!evt.PageInstance.MoveRoute.Complete) break;
                                            curStack.WaitingForRoute = Guid.Empty;
                                            curStack.WaitingForRouteMap = Guid.Empty;
                                            break;
                                        }
                                    }
                                }
                                if (curStack.WaitingForRoute != Guid.Empty) break;
                            }
                            else
                            {
                                if (curStack.CommandIndex >= curStack.CommandList.Count)
                                {
                                    CallStack.Pop();
                                }
                                else
                                {
                                    if (WaitTimer < Globals.System.GetTimeMs())
                                    {
                                        CommandProcessing.ProcessCommand(curStack.Command,MyPlayer,this);
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                                if (CallStack.Count == 0)
                                {
                                    PlayerHasDied = false;
                                    NpcDeathTriggerd = true;
                                    break;
                                }
                            }
                            curStack = CallStack.Peek();
                        }
                    }
                    else
                    {
                        if (PageInstance.Trigger == EventTrigger.Autorun)
                        {
                            var newStack = new CommandInstance(PageInstance.MyPage);
                            CallStack.Push(newStack);
                        }
                    }
                }
            }

            if (PageInstance == null)
            {
                //Try to Spawn a PageInstance.. if we can
                for (int i = BaseEvent.Pages.Count - 1; i >= 0; i--)
                {
                    if (Conditions.CanSpawnPage(BaseEvent.Pages[i],MyPlayer,this))
                    {
                        if (Global)
                        {
                            if (MapInstance.Get(MapId).GetGlobalEventInstance(BaseEvent) != null)
                            {
                                PageInstance = new EventPageInstance(BaseEvent, BaseEvent.Pages[i],BaseEvent.Id,MapId, this, MyClient, MapInstance.Get(MapId).GetGlobalEventInstance(BaseEvent).GlobalPageInstance[i]);
                                sendLeave = false;
                                PageIndex = i;
                            }
                        }
                        else
                        {
                            PageInstance = new EventPageInstance(BaseEvent, BaseEvent.Pages[i], MapId, this, MyClient);
                            sendLeave = false;
                            PageIndex = i;
                        }
                        break;
                    }
                }

                if (sendLeave)
                {
                    PacketSender.SendEntityLeaveTo(MyClient, BaseEvent.Id, (int)EntityTypes.Event, MapId);
                }
            }
        }
    }

    public class CommandInstance
    {
        public enum EventResponse
        {
            None = 0,
            Dialogue,
            Shop,
            Bank,
            Crafting,
            Quest,
        }

        private int commandIndex;
        public int CommandIndex
        {
            get { return commandIndex; }
            set
            {
                commandIndex = value;
                Command = commandIndex >= 0 && commandIndex < CommandList.Count ? CommandList[commandIndex] : null;
            }
        }
        public EventCommand Command;
        public Guid CommandListId;
        public List<EventCommand> CommandList;

        public EventResponse WaitingForResponse = EventResponse.None;
        public EventCommand WaitingOnCommand = null;
        public Guid[] BranchIds = null; //Potential Branches for Commands that require responses such as ShowingOptions or Offering a Quest

        public EventPage Page;


        public Guid WaitingForRoute;
        public Guid WaitingForRouteMap;

        public CommandInstance(EventPage page, int listIndex = 0)
        {
            Page = page;
            CommandList = page.CommandLists.Values.First();
            CommandIndex = listIndex;
        }

        public CommandInstance(EventPage page, List<EventCommand> commandList, int listIndex = 0)
        {
            Page = page;
            CommandList = commandList;
            CommandIndex = listIndex;
        }

        public CommandInstance(EventPage page, Guid commandListId, int listIndex = 0)
        {
            Page = page;
            CommandList = page.CommandLists[commandListId];
            CommandIndex = listIndex;

        }
    }
}