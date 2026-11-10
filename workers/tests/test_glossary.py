import pytest

from iris_workers.translation.glossary import GlossaryEntry, TermProtector


def protect(text: str, entries=None):
    protector = TermProtector() if entries is None else TermProtector(entries)
    return protector.protect(text)


def test_replaces_terms_with_placeholders_longest_first() -> None:
    result = protect("Can you open a pull request so we can review it before the merge?")

    assert result.text == "Can you open a ZQX0 so we can review it before the ZQX1?"
    assert [(s[1], s[2]) for s in result.slots] == [("pull request", "m"), ("merge", "m")]


def test_matching_is_case_insensitive_and_keeps_surface_form_and_plural() -> None:
    result = protect("Two Bugs and one TIMEOUT.")

    assert result.text == "Two ZQX0 and one ZQX1."
    assert [s[1] for s in result.slots] == ["Bugs", "TIMEOUT"]


def test_acronyms_are_case_sensitive() -> None:
    assert protect("Open a PR for it.").text == "Open a ZQX0 for it."
    assert protect("pr is not an acronym here").slots == ()


def test_terms_inside_other_words_are_not_matched() -> None:
    assert protect("The debugger and the rebuild-tool and cached data.").slots == ()


@pytest.mark.parametrize(
    ("text", "protected"),
    [
        ("The build has been failing.", True),
        ("Our nightly build is red.", True),
        ("We need to build a tool.", False),
        ("Let's deploy it on Friday.", False),
        ("We rolled back after the deploy.", True),
    ],
)
def test_noun_only_terms_need_a_determiner(text: str, protected: bool) -> None:
    assert bool(protect(text).slots) is protected


def test_noun_only_keeps_the_determiner_in_the_source() -> None:
    assert protect("Check the nightly build now.").text == "Check the nightly ZQX0 now."


def test_restore_puts_terms_back_and_fixes_feminine_determiners() -> None:
    slots = protect("Keep it behind a feature flag during the sprint.").slots

    restored = TermProtector.restore("Mantenha atrás de um ZQX0 durante o ZQX1.", slots)

    assert restored.missing == ()
    assert restored.text == "Mantenha atrás de uma feature flag durante a sprint."


def test_restore_fixes_capitalized_contractions() -> None:
    slots = protect("The sprint ended.").slots

    assert TermProtector.restore("O ZQX0 terminou.", slots).text == "A sprint terminou."
    assert TermProtector.restore("Do ZQX0, nada.", slots).text == "Da sprint, nada."


def test_restore_reports_lost_or_duplicated_placeholders() -> None:
    slots = protect("Fix the bug in the dashboard.").slots

    assert TermProtector.restore("Corrija o erro no ZQX1.", slots).missing == ("ZQX0",)
    assert TermProtector.restore("ZQX0 ZQX0 no ZQX1.", slots).missing == ("ZQX0",)


def test_restore_does_not_confuse_zqx1_with_zqx10() -> None:
    entries = [GlossaryEntry(f"term{i}") for i in range(11)]
    text = " ".join(f"term{i}" for i in range(11))
    slots = protect(text, entries).slots

    restored = TermProtector.restore(" ".join(f"ZQX{i}" for i in range(11)), slots)

    assert restored.missing == ()
    assert restored.text == text


def test_empty_glossary_leaves_text_untouched() -> None:
    assert protect("The build failed.", []).text == "The build failed."
