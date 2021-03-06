﻿using System;
using System.Collections.Generic;
using System.Threading;
using Aws.GameLift;
using Aws.GameLift.Server;
using Aws.GameLift.Server.Model;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace NetworkingScripts.Server {
  public class GameLiftAdapter {
    private readonly Dictionary<ulong, string> playerSessions = new Dictionary<ulong, string>();
    public event EventHandler<GameSession> GameSessionStarted;
    private CancellationTokenSource idleCancelTokenSource;


    //This is an example of a simple integration with GameLift server SDK that makes game server 
    //processes go active on Amazon GameLift
    // Called by Monobehaviour entrypoint Start
    public void Init() {
      //InitSDK establishes a local connection with the Amazon GameLift agent to enable 
      //further communication.
      //This method should be called on launch, before any other GameLift-related initialization occurs.
      var initSDKOutcome = GameLiftServerAPI.InitSDK();
      if (initSDKOutcome.Success) {
        ProcessReady();
        // TODO: Probably want to do some control flow for outcome.Error
      }
      else {
        Debug.Log($"InitSDK failure : {initSDKOutcome.Error}");
      }
    }

    private void ProcessReady() {
      //Set the port that your game service is listening on for incoming player connections
      //In this example, the port is hardcoded for simplicity. Active games
      //that are on the same instance must have unique ports.
      const int listeningPort = 7777; // TODO: port picking logic?

      //Here, the game server tells GameLift what set of files to upload when the game session ends.
      //GameLift uploads everything specified here for the developers to fetch later.
      // must be different for each server if multiple servers on instance
      var logParameters = new LogParameters(new List<string> {"/local/game/myserver.log"});

      var processParameters = new ProcessParameters(
        OnStartGameSession,
        OnProcessTerminate,
        OnHealthCheckDelegate,
        listeningPort,
        logParameters);

      //Notifies the GameLift service that the server process is ready to host game sessions.
      //Call this method after successfully invoking InitSDK() and completing setup tasks that are required
      //before the server process can host a game session. This method should be called only once per process.
      //Calling ProcessReady tells GameLift this game server is ready to receive incoming game sessions!
      var processReadyOutcome = GameLiftServerAPI.ProcessReady(processParameters);
      Debug.Log(processReadyOutcome.Success
        ? "ProcessReady success."
        : $"ProcessReady failure : {processReadyOutcome.Error}");
    }

    //Respond to new game session activation request. GameLift sends activation request 
    //to the game server along with a game session object containing game properties 
    //and other settings. Once the game server is ready to receive player connections, 
    //invoke GameLiftServerAPI.ActivateGameSession()
    private void OnStartGameSession(GameSession gameSession) {
      Debug.Log(":) GAMELIFT SESSION REQUESTED"); //And then do stuff with it maybe.
      try {
        //Notifies the GameLift service that the server process has activated a game session and is now ready to
        //receive player connections. This action should be called as part of the onStartGameSession() callback
        //function, after all game session initialization has been completed.
        var outcome = GameLiftServerAPI.ActivateGameSession();
        if (outcome.Success) {
          Debug.Log(":) GAME SESSION ACTIVATED");
          GameSessionStarted?.Invoke(this, gameSession);
          TerminateIdleGameSession().Forget();
        }
        else {
          Debug.Log($":( GAME SESSION ACTIVATION FAILED. ActivateGameSession() returned {outcome.Error}");
        }
      }
      catch (Exception e) {
        Debug.Log(
          $":( GAME SESSION ACTIVATION FAILED. ActivateGameSession() exception \n{e.Message}");
      }
    }

    private void OnProcessTerminate() {
      //OnProcessTerminate callback. GameLift invokes this callback before shutting down 
      //an instance hosting this game server. It gives this game server a chance to save
      //its state, communicate with services, etc., before being shut down. 
      //In this case, we simply tell GameLift we are indeed going to shut down.
      Debug.Log(":| GAMELIFT PROCESS TERMINATION REQUESTED (OK BYE)");
      GameLiftServerAPI.ProcessEnding();
    }

    private bool OnHealthCheckDelegate() {
      //This is the HealthCheck callback.
      //GameLift invokes this callback every 60 seconds or so.
      //Here, a game server might want to check the health of dependencies and such.
      //Simply return true if healthy, false otherwise.
      //The game server has 60 seconds to respond with its health status. 
      //GameLift will default to 'false' if the game server doesn't respond in time.
      //In this case, we're always healthy!
      Debug.Log(":) GAMELIFT HEALTH CHECK REQUESTED (HEALTHY)");
      return true;
    }

    private async UniTaskVoid TerminateIdleGameSession() {
      // Cancel game session after 60 seconds of idle.
      idleCancelTokenSource = new CancellationTokenSource();
      try {
        await UniTask.Delay(TimeSpan.FromSeconds(120), cancellationToken: idleCancelTokenSource.Token);
        Debug.Log(":) OK WE WAITED TOO LONG BYEEE");
        GameLiftServerAPI.ProcessEnding();
      }
      catch (Exception e) when (e is OperationCanceledException) {
        // DO NOTHING, it cancelled.
        Debug.Log(":) NOT IDLE ANYMORE, CARRY ON");
      }
    }

    private void OnApplicationQuit() {
      //Notifies the GameLift service that the server process is shutting down. This method should be called
      //AFTER all other cleanup tasks, including shutting down all active game sessions.
      //This method should exit with an exit code of 0;
      //a non-zero exit code results in an event message that the process did not exit cleanly.
      var outcome = GameLiftServerAPI.ProcessEnding();
      Debug.Log(outcome.Success
        ? ":) PROCESSENDING"
        : $":( PROCESSENDING FAILED. ProcessEnding() returned {outcome.Error}");

      //Make sure to call GameLiftServerAPI.Destroy() when the application quits. 
      //This resets the local connection with GameLift's agent.
      GameLiftServerAPI.Destroy();
    }

    // Called from MLAPI on new client connection
    public bool ConnectPlayer(ulong clientId, string playerSessionId) {
      try {
        //Notifies the GameLift service that a player with the specified player session ID has connected to the
        //server process and needs validation. GameLift verifies that the player session ID is valid—that is,
        //that the player ID has reserved a player slot in the game session. Once validated,
        //GameLift changes the status of the player slot from RESERVED to ACTIVE.
        var outcome = GameLiftServerAPI.AcceptPlayerSession(playerSessionId);
        Debug.Log(outcome.Success
          ? ":) PLAYER SESSION VALIDATED"
          : $":( PLAYER SESSION REJECTED. AcceptPlayerSession() returned {outcome.Error}");

        playerSessions.Add(clientId, playerSessionId);
        idleCancelTokenSource.Cancel();
        return outcome.Success;
      }
      catch (Exception e) {
        Debug.Log($":( REJECTED PLAYER SESSION. AcceptPlayerSession() exception \n{e.Message}");
        return false;
      }
    }

    // Called from MLAPI on client disconnect
    // Called on MLAPI connection exception
    public void DisconnectPlayer(ulong clientId) {
      // if player slots never re-open, just skip this entire thing.
      try {
        var playerSessionId = playerSessions[clientId];
        try {
          //Notifies the GameLift service that a player with the specified player session ID has disconnected
          //from the server process. In response, GameLift changes the player slot to available,
          //which allows it to be assigned to a new player.
          var outcome = GameLiftServerAPI.RemovePlayerSession(playerSessionId);
          GameLiftServerAPI.ProcessEnding(); // For now, killing game session on player leaving.          
          Debug.Log(outcome.Success
            ? ":) PLAYER SESSION REMOVED"
            : $":( PLAYER SESSION REMOVE FAILED. RemovePlayerSession() returned {outcome.Error}");
        }
        catch (Exception e) {
          Debug.Log($":( PLAYER SESSION REMOVE FAILED. RemovePlayerSession() exception\n{e.Message}");
          throw;
        }

        playerSessions.Remove(clientId);
      }
      catch (KeyNotFoundException e) {
        Debug.Log($":( INVALID PLAYER SESSION. Exception \n{e.Message}");
        throw; // should never happen
      }
    }
  }
}
