from iris_workers.speech.vocabulary import extract_terms

RARE = {"refactor", "microservices", "ergonomic", "francina"}


def tokens(word: str) -> int:  # stand-in for Whisper's BPE: listed words are rare (2+ tokens)
    return 2 if word in RARE else 1


def kinds(sentence: str) -> dict[str, str]:
    return {t.text: t.kind for t in extract_terms(sentence, tokens)}


def test_rare_words_names_and_acronyms_are_candidates():
    assert kinds("We should refactor the microservices before the OKR review with Francina.") == {
        "refactor": "rare", "microservices": "rare", "OKR": "acronym", "Francina": "name"}


def test_common_words_and_sentence_initial_capitals_are_not():
    assert kinds("Okay so the meeting is on Monday and I think it went well.") == {}
    assert kinds("Sarah said yes.") == {}           # first word: capitalized because it starts the sentence
    assert kinds("I talked to Sarah.") == {"Sarah": "name"}


def test_possessives_duplicates_and_short_words():
    assert kinds("Then Kubernetes's scheduler, Kubernetes again, and an A plan.") == {"Kubernetes": "name"}


def test_rare_words_are_lowercased_and_need_four_letters():
    assert kinds("The Ergonomic shape matters.") == {"Ergonomic": "name"}  # capitalized mid-sentence: a name
    assert kinds("ergonomic grips.") == {"ergonomic": "rare"}
