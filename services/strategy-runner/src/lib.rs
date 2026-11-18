//! QuantDesk runner bootstrap. No strategy container launching or exchange routing yet.
pub fn runner_component_name() -> &'static str { "quantdesk-strategy-runner" }

#[cfg(test)]
mod tests {
    #[test]
    fn bootstrap_name_is_stable() {
        assert_eq!(super::runner_component_name(), "quantdesk-strategy-runner");
    }
}