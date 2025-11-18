using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    // Minimal HUD to visualize Health and Stamina using Unity UI Sliders.
    // Attach this to a GameObject under a Canvas and assign references.
    public sealed class StatsHUD : MonoBehaviour
    {
        [Header("Targets")]
        [Tooltip("Health component to read from. If null, searches on Player Root.")]
        [SerializeField] private Characters.Health.Health health;
        [Tooltip("Stamina component to read from. If null, searches on Player Root.")]
        [SerializeField] private Characters.Stats.Stamina stamina;
        [Tooltip("Optional: Player root to auto-find Health/Stamina.")]
        [SerializeField] private Transform playerRoot;

        [Header("UI Elements")]
        [Tooltip("UI Slider showing Health (0..1 normalized).")]
        [SerializeField] private Slider healthSlider;
        [Tooltip("UI Slider showing Stamina (0..1 normalized).")]
        [SerializeField] private Slider staminaSlider;
        [Tooltip("Optional Text for numeric Health.")]
        [SerializeField] private TMPro.TMP_Text healthText;
        [Tooltip("Optional Text for numeric Stamina.")]
        [SerializeField] private TMPro.TMP_Text staminaText;

        [Header("State Readouts (Optional)")]
        [Tooltip("Movement component to read state from.")]
        [SerializeField] private Characters.Movement.CharacterMovement movement;
        [Tooltip("Combat component to read attack phase and hit confirms from.")]
        [SerializeField] private Characters.Combat.CharacterCombat combat;
        [SerializeField] private TMPro.TMP_Text movementStateText;
        [SerializeField] private TMPro.TMP_Text attackStateText;
        [SerializeField] private TMPro.TMP_Text hitConfirmText;

        private int hitConfirmCount;

        private void Awake()
        {
            if (playerRoot == null)
            {
                // Try a reasonable default: main camera's Follow target often is the player, but fallback to scene tagged Player
                var tagged = GameObject.FindWithTag("Player");
                if (tagged != null) playerRoot = tagged.transform;
            }

            if (health == null && playerRoot != null)
                health = playerRoot.GetComponentInChildren<Characters.Health.Health>();
            if (stamina == null && playerRoot != null)
                stamina = playerRoot.GetComponentInChildren<Characters.Stats.Stamina>();

            if (movement == null && playerRoot != null)
                movement = playerRoot.GetComponentInChildren<Characters.Movement.CharacterMovement>();
            if (combat == null && playerRoot != null)
                combat = playerRoot.GetComponentInChildren<Characters.Combat.CharacterCombat>();
        }

        private void OnEnable()
        {
            if (health != null) health.OnHealthChanged += OnHealthChanged;
            if (stamina != null) stamina.OnStaminaChanged += OnStaminaChanged;
            if (combat != null) combat.OnHitConfirmed += OnHitConfirmed;

            // Initialize UI immediately
            UpdateHealthUI();
            UpdateStaminaUI();
            UpdateStateUI();
        }

        private void OnDisable()
        {
            if (health != null) health.OnHealthChanged -= OnHealthChanged;
            if (stamina != null) stamina.OnStaminaChanged -= OnStaminaChanged;
            if (combat != null) combat.OnHitConfirmed -= OnHitConfirmed;
        }

        private void OnHealthChanged(float current, float max) => UpdateHealthUI();
        private void OnStaminaChanged(float current, float max) => UpdateStaminaUI();
        private void OnHitConfirmed(GameObject attacker, GameObject target, float dmg)
        {
            hitConfirmCount++;
            UpdateStateUI();
        }

        private void Update()
        {
            // Fallback polling in case events are missed
            UpdateHealthUI();
            UpdateStaminaUI();
            UpdateStateUI();
        }

        private void UpdateHealthUI()
        {
            if (healthSlider != null && health != null)
            {
                float v = Mathf.Approximately(health.Max, 0f) ? 0f : (health.Current / health.Max);
                healthSlider.normalizedValue = Mathf.Clamp01(v);
            }
            if (healthText != null && health != null)
            {
                healthText.text = $"HP: {Mathf.CeilToInt(health.Current)}/{Mathf.CeilToInt(health.Max)}";
            }
        }

        private void UpdateStaminaUI()
        {
            if (staminaSlider != null && stamina != null)
            {
                float max = Mathf.Max(1f, stamina.Max);
                float v = stamina.Current / max;
                staminaSlider.normalizedValue = Mathf.Clamp01(v);
            }
            if (staminaText != null && stamina != null)
            {
                staminaText.text = $"STA: {Mathf.CeilToInt(stamina.Current)}/{Mathf.CeilToInt(stamina.Max)}";
            }
        }

        private void UpdateStateUI()
        {
            if (movementStateText != null && movement != null)
            {
                var field = typeof(Characters.Movement.CharacterMovement).GetField("state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    var st = field.GetValue(movement);
                    movementStateText.text = $"State: {st}";
                }
            }
            if (attackStateText != null && combat != null)
            {
                attackStateText.text = $"Attack: {combat.CurrentPhase}";
            }
            if (hitConfirmText != null)
            {
                hitConfirmText.text = $"Hits: {hitConfirmCount}";
            }
        }
    }
}

