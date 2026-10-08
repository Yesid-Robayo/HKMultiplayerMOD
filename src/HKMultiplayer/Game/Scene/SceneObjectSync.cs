using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Game.World;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Objetos de la sala en directo: cuando uno rompe una pared, tira de una palanca o abre una puerta, los demás que están
/// en la misma sala lo ven al momento.
/// <para>
/// Los objetos guardables del juego (<see cref="PersistentBoolItem"/>) llevan una variable "Activated" en su FSM. Se
/// vigila cada frame (sin crear memoria) y, al activarse en este PC, se avisa a la sala. Al recibirlo, el objeto se
/// pone en el estado al que el propio juego lo lleva al cargar la sala con él ya activado (se busca en su FSM la
/// comprobación de "Activated"), así queda igual que si se hubiera roto aquí.
/// </para>
/// Los objetos que recoge cada uno (<see cref="CharacterData.IsIndividualItem"/>) quedan fuera.
/// </summary>
internal sealed class SceneObjectSync {
    public const MessageType ObjectActivated = (MessageType) 42;

    private const string ActivatedVariable = "Activated";

    private sealed class Tracked {
        public string Id;
        public PlayMakerFSM Fsm;
        public FsmBool Activated;
        public bool LastValue;
        public string ActivatedState;
    }

    private readonly List<Tracked> _tracked = new();
    private readonly Dictionary<string, Tracked> _byId = new();
    private readonly MessageHandler _onActivated;
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;

    static SceneObjectSync() {
        MessageRoutes.Register(ObjectActivated, Route.SameScene, reliable: true);
    }

    public SceneObjectSync() {
        _onActivated = OnActivated;
    }

    public int TrackedCount => _tracked.Count;

    public void Attach(GameSession session) {
        _session = session;
        session.On(ObjectActivated, _onActivated);
        Clear();
    }

    public void Detach() {
        _session?.On(ObjectActivated, null);
        _session = null;
        Clear();
    }

    private void Clear() {
        _tracked.Clear();
        _byId.Clear();
        _scene = string.Empty;
        _sceneHash = 0;
    }

    /// <summary>Llamar cada frame con la sala local (vacía si no se está jugando).</summary>
    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            Clear();
            _scene = scene;
            _sceneHash = PlayerSnapshot.HashScene(scene);
            if (scene.Length == 0) {
                return;
            }

            Scan();
            // Hay objetos que se activan un poco después de cargar la sala: segunda pasada
            _rescanAt = Time.unscaledTime + 1.5f;
            return;
        }

        if (scene.Length == 0) {
            return;
        }

        if (_rescanAt > 0f && Time.unscaledTime >= _rescanAt) {
            _rescanAt = 0f;
            Scan();
        }

        for (var i = 0; i < _tracked.Count; i++) {
            var item = _tracked[i];
            if (item.Fsm == null) {
                continue;
            }

            var value = item.Activated.Value;
            if (value == item.LastValue) {
                continue;
            }

            item.LastValue = value;
            if (value && _session.CanSend) {
                var writer = _session.Begin(ObjectActivated);
                writer.WriteUShort(_sceneHash);
                writer.WriteString(item.Id);
                _session.End();
            }
        }
    }

    /// <summary>Busca los objetos guardables de la sala (solo crea memoria al entrar en una sala).</summary>
    private void Scan() {
        foreach (var persistent in Object.FindObjectsByType<PersistentBoolItem>(FindObjectsSortMode.None)) {
            var id = persistent.persistentBoolData?.id;
            if (string.IsNullOrEmpty(id)) {
                id = persistent.name;
            }

            if (_byId.ContainsKey(id) || CharacterData.IsIndividualItem(id)) {
                continue;
            }

            var fsm = FSMUtility.FindFSMWithPersistentBool(persistent.GetComponents<PlayMakerFSM>());
            var activated = fsm != null ? fsm.FsmVariables.FindFsmBool(ActivatedVariable) : null;
            if (activated == null) {
                continue;
            }

            var tracked = new Tracked {
                Id = id,
                Fsm = fsm,
                Activated = activated,
                LastValue = activated.Value,
                ActivatedState = FindActivatedState(fsm)
            };
            _tracked.Add(tracked);
            _byId[id] = tracked;
        }
    }

    /// <summary>
    /// Estado al que va la FSM cuando "Activated" es verdadero al comprobarlo (lo que hace el juego al cargar la sala
    /// con el objeto ya activado). Se prefiere la comprobación más cercana al estado inicial.
    /// </summary>
    private static string FindActivatedState(PlayMakerFSM fsm) {
        var states = fsm.FsmStates;
        var start = fsm.Fsm.StartState;
        string fallback = null;
        foreach (var state in states) {
            foreach (var action in state.Actions) {
                if (action is not BoolTest test || test.boolVariable == null ||
                    test.boolVariable.Name != ActivatedVariable || test.isTrue == null) {
                    continue;
                }

                foreach (var transition in state.Transitions) {
                    if (transition.FsmEvent == test.isTrue && !string.IsNullOrEmpty(transition.ToState)) {
                        if (state.Name == start) {
                            return transition.ToState;
                        }

                        fallback ??= transition.ToState;
                    }
                }
            }
        }

        return fallback;
    }

    private void OnActivated(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadString(256);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null) {
            return;
        }

        Activate(item);
        Log.Info($"{from.Name} activó {id} en esta sala");
    }

    /// <summary>Deja el objeto como activado, igual que al cargar la sala con él activado.</summary>
    private static void Activate(Tracked item) {
        if (item.Activated.Value) {
            item.LastValue = true;
            return;
        }

        item.Activated.Value = true;
        item.LastValue = true;
        if (item.ActivatedState != null) {
            item.Fsm.SetState(item.ActivatedState);
        }
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    /// <summary>Lista de objetos vigilados de la sala (id, activado, estado de activado).</summary>
    public IEnumerable<string> Describe() {
        foreach (var item in _tracked) {
            yield return $"{item.Id}={(item.Activated.Value ? "sí" : "no")}→{item.ActivatedState ?? "?"} ({item.Fsm.ActiveStateName})";
        }
    }

    /// <summary>Activa un objeto como si lo hubiera hecho este jugador (se avisa a la sala).</summary>
    public bool TestActivate(string idPrefix) {
        foreach (var item in _tracked) {
            if (item.Id.StartsWith(idPrefix) && !item.Activated.Value) {
                item.Activated.Value = true;
                if (item.ActivatedState != null) {
                    item.Fsm.SetState(item.ActivatedState);
                }

                return true;
            }
        }

        return false;
    }
}
