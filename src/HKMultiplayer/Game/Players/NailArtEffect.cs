using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// En la copia de un arte del aguijón (Great Slash, Dash Slash, Cyclone Slash) hace lo visual de la FSM que lo mueve en
/// el caballero, que se quita porque también enciende su colisión: al activarse se coloca y se anima, y se apaga al
/// terminar. El ciclón gira hasta que llega su fin (<see cref="End"/>, el aviso "CYCLONE END").
/// </summary>
internal sealed class NailArtEffect : MonoBehaviour {
    public tk2dSpriteAnimationClip Clip;
    public tk2dSpriteAnimationClip EndClip;
    public Vector3 Position;
    public Vector3 Scale;

    private tk2dSpriteAnimator _animator;
    private MeshRenderer _renderer;

    private void Awake() {
        _animator = GetComponent<tk2dSpriteAnimator>();
        _renderer = GetComponent<MeshRenderer>();
        if (_animator != null) {
            _animator.AnimationCompleted = OnCompleted;
        }
    }

    private void OnEnable() {
        var self = transform;
        self.localPosition = Position;
        self.localScale = Scale;
        if (_renderer != null) {
            _renderer.enabled = true;
        }

        if (_animator != null && Clip != null) {
            _animator.PlayFromFrame(Clip, 0);
        }
    }

    /// <summary>Termina el efecto: su animación de fin, si tiene, y se apaga.</summary>
    public void End() {
        if (!isActiveAndEnabled) {
            return;
        }

        if (_animator != null && EndClip != null) {
            _animator.PlayFromFrame(EndClip, 0);
        } else {
            gameObject.SetActive(false);
        }
    }

    private void OnCompleted(tk2dSpriteAnimator animator, tk2dSpriteAnimationClip clip) {
        if (clip == EndClip || EndClip == null) {
            gameObject.SetActive(false);
        }
    }
}
