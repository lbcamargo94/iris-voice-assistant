from iris_workers.translation.refine_guard import check_refinement


def test_accepts_wording_change_that_keeps_facts():
    result = check_refinement(
        "O build está falhando desde esta manhã por causa de um teste escamoso.",
        "O build está falhando desde esta manhã devido a um teste instável.",
        ["build"],
    )
    assert result.accepted
    assert result.reasons == ()


def test_rejects_changed_weekday_and_dropped_relative_date():
    result = check_refinement(
        "Vou marcar uma reunião para a próxima terça-feira às dez.",
        "Vou marcar uma reunião para segunda-feira às dez.",
    )
    assert not result.accepted
    assert "weekdays" in result.reasons
    assert "relative_time" in result.reasons


def test_weekday_short_and_long_forms_are_equal():
    assert check_refinement("Entregamos na sexta.", "Vamos entregar na sexta-feira.").accepted


def test_rejects_vague_quantity_turned_into_number():
    result = check_refinement("por algumas semanas.", "por duas semanas.")
    assert result.reasons == ("numbers",)


def test_rejects_changed_digits():
    result = check_refinement("Passou de 300 ms para 120.", "Passou de 300 ms para 210.")
    assert result.reasons == ("numbers",)


def test_article_um_is_not_a_number():
    assert check_refinement("Adiar em uma semana.", "Adiar por uma semana.").accepted


def test_rejects_dropped_negation():
    result = check_refinement("Não tive tempo de ver o bug.", "Tive tempo de ver o bug.", ["bug"])
    assert result.reasons == ("negation",)


def test_rejects_changed_name_but_allows_name_moving_to_sentence_start():
    assert check_refinement("Estou de acordo com o Alex.", "Estou de acordo com o Mark.").reasons == ("names",)
    assert check_refinement("Concordo com o Alex nisso.", "Alex, concordo com você nisso.").accepted


def test_rejects_lost_glossary_term():
    result = check_refinement("O deploy falhou.", "A implantação falhou.", ["deploy"])
    assert result.reasons == ("glossary",)


def test_terms_absent_from_fast_are_not_required():
    assert check_refinement("A implantação falhou.", "A implantação deu erro.", ["deploy"]).accepted


def test_rejects_question_turned_into_statement():
    result = check_refinement("Pode repetir a última parte?", "Pode repetir a última parte.")
    assert result.reasons == ("questions",)


def test_rejects_empty_unclean_and_pt_pt_output():
    assert check_refinement("Tudo certo.", "  ").reasons == ("empty",)
    assert "unclean_output" in check_refinement("Tudo certo.", "Tudo certo.", clean=False).reasons
    assert "pt_pt" in check_refinement("Abra o arquivo.", "Abra o ficheiro.").reasons


def test_rejects_large_length_change():
    assert "length" in check_refinement("Sim.", "Sim, com certeza, vamos fazer isso amanhã cedo.").reasons
