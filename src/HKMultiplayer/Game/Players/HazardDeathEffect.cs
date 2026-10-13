using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Muerte de un compañero en pinchos o en ácido, solo visual: lo que hace la FSM de <c>spikeDeathPrefab</c>
/// ("Knight Death Control") y de <c>acidDeathPrefab</c> ("Knight Acid Death") sin su lógica (guardar la sala, avisar al
/// caballero, la cámara). En pinchos se queda clavado 0,3 s y sale despedido hacia arriba; en ácido se hunde frenando.
/// </summary>
internal sealed class HazardDeathEffect : MonoBehaviour {
    /// <summary>Lo que se ve como mucho: el caballero reaparece antes en el último punto seguro.</summary>
    private const float Life = 2f;

    /// <summary>Lo que se queda clavado antes de salir despedido (el Wait de su estado "Stab").</summary>
    private const float SpikeHold = 0.3f;

    /// <summary>Velocidad y gravedad con las que sale despedido (estado "Fling").</summary>
    private const float FlingSpeed = 20f;

    private const float FlingGravity = 0.7f;

    /// <summary>Cómo se hunde en el ácido: a 8 hacia abajo, frenando 0,9 por paso de física (DecelerateV2).</summary>
    private const float SinkSpeed = 8f;

    private const float SinkDeceleration = 0.9f;

    public bool Spikes;

    private tk2dSpriteAnimator _animator;
    private Vector2 _velocity;
    private float _age;
    private bool _flung;
    private float _scaleX;

    /// <summary>Empieza en ese punto, mirando como el caballero (su escala en x).</summary>
    public void Play(Vector3 position, float scaleX) {
        gameObject.SetActive(false);
        var transform = this.transform;
        transform.position = position;
        _scaleX = scaleX;
        var scale = transform.localScale;
        transform.localScale = new Vector3(scaleX, scale.y, scale.z);
        _age = 0f;
        _flung = false;
        _velocity = Spikes ? Vector2.zero : new Vector2(0f, -SinkSpeed);
        gameObject.SetActive(true);

        // Sus hijos (las púas que lo atraviesan; en el ácido la salpicadura, el vapor y la grieta) empiezan de nuevo
        foreach (var child in GetComponentsInChildren<Transform>(true)) {
            child.gameObject.SetActive(true);
        }

        foreach (var animator in GetComponentsInChildren<tk2dSpriteAnimator>(true)) {
            var renderer = animator.GetComponent<MeshRenderer>();
            if (renderer != null) {
                renderer.enabled = true;
            }

            if (animator.gameObject != gameObject && animator.DefaultClip != null) {
                animator.Play(animator.DefaultClip);
            }
        }

        foreach (var particles in GetComponentsInChildren<ParticleSystem>(true)) {
            particles.Play();
        }

        _animator ??= GetComponent<tk2dSpriteAnimator>();
        if (_animator != null) {
            var clip = Spikes ? _animator.GetClipByName("Spike Death Antic") : _animator.DefaultClip;
            if (clip != null) {
                _animator.Play(clip);
            }
        }
    }

    private void Update() {
        var deltaTime = Time.deltaTime;
        _age += deltaTime;
        if (_age >= Life) {
            gameObject.SetActive(false);
            return;
        }

        var transform = this.transform;
        if (Spikes) {
            if (!_flung) {
                if (_age < SpikeHold) {
                    return;
                }

                _flung = true;
                var clip = _animator != null ? _animator.GetClipByName("Spike Death") : null;
                if (clip != null) {
                    _animator.Play(clip);
                }

                var angle = Random.Range(80f, 100f) * Mathf.Deg2Rad;
                _velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * FlingSpeed;
            }

            _velocity.y += Physics2D.gravity.y * FlingGravity * deltaTime;

            // Mira hacia donde vuela (FaceDirection con el sprite mirando a la derecha)
            var scale = transform.localScale;
            var facing = Mathf.Abs(_scaleX) * (_velocity.x >= 0f ? 1f : -1f);
            if (!Mathf.Approximately(scale.x, facing)) {
                transform.localScale = new Vector3(facing, scale.y, scale.z);
            }
        } else {
            _velocity *= Mathf.Pow(SinkDeceleration, deltaTime / Time.fixedDeltaTime);
        }

        transform.position += (Vector3) (_velocity * deltaTime);
    }
}
