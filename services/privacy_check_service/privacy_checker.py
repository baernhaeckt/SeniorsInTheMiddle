import logging
import os
from typing import Any

import numpy as np
import pymc as pm
from sentence_transformers import SentenceTransformer

logger = logging.getLogger(__name__)

class PrivacyChecker:
    def __init__(self):
        """
        Initializes the PrivacyChecker class.
        """
        logger.info(f"Start downloading embedding model")
        self.encoder_model = SentenceTransformer("sentence-transformers/all-MiniLM-L6-v2")

    @staticmethod
    def _levenshtein(a: str, b: str, ratio: bool = True, print_matrix: bool = False, lowercase: bool = False):
        """Compute Levenshtein distance or similarity ratio between two strings.

        Args:
            a, b: input strings.
            ratio: if True return similarity in [0.0, 1.0], else return integer distance.
            print_matrix: if True prints the DP matrix.
            lowercase: if True compares lowercase variants.

        Returns:
            float similarity (if ratio=True) or int distance (if ratio=False).
        """
        if not isinstance(a, str):
            raise TypeError("First argument must be a string")
        if not isinstance(b, str):
            raise TypeError("Second argument must be a string")

        if lowercase:
            a = a.lower()
            b = b.lower()

        n, m = len(a), len(b)

        # fast returns for empty inputs
        if n == 0 and m == 0:
            return 1.0 if ratio else 0
        if n == 0:
            return 0.0 if ratio else m
        if m == 0:
            return 0.0 if ratio else n

        # build full DP matrix (rows: 0..n, cols: 0..m)
        mat = [[0] * (m + 1) for _ in range(n + 1)]
        for i in range(n + 1):
            mat[i][0] = i
        for j in range(m + 1):
            mat[0][j] = j

        for i in range(1, n + 1):
            ai = a[i - 1]
            row = mat[i]
            prev_row = mat[i - 1]
            for j in range(1, m + 1):
                cost = 0 if ai == b[j - 1] else 1
                row[j] = min(prev_row[j] + 1,  # insertion
                             row[j - 1] + 1,  # deletion
                             prev_row[j - 1] + cost)  # substitution

        dist = mat[n][m]

        if print_matrix:
            for r in mat:
                print(r)

        if ratio:
            # similarity in [0,1]; avoid division by zero already handled above
            return (n + m - dist) / (n + m)
        return dist

    @staticmethod
    def _make_features(similarities: Any, query: str, candidate_name: str, embedding_similarity: float) -> list[Any]:
        """
        Creates a feature vector for the given query and candidate name.
        Args:
            similarities (Any): A series of similarity scores.
            query (str): The input query string.
            candidate_name (str): The candidate name string.
            embedding_similarity (float): The embedding similarity score between the query and candidate name.
        """
        return [
            embedding_similarity,

            # Exact match
            float(candidate_name.lower() in query.lower()),

            # Character similarity
            PrivacyChecker._levenshtein(query, candidate_name),

            # z-score of the similarity (0 for a single candidate, where std is 0)
            (embedding_similarity - similarities.mean()) / std if (std := float(similarities.std())) > 0 else 0.0
        ]

    @staticmethod
    def _calculate_probability(features_list: np.ndarray) -> Any:
        """
        A Bayesian model to calculate the probability risk that the data can be reproduced
        Args:
            features_list (np.ndarray): A numpy array of the features vectors.
        Returns:
            (Any): The probability risk that the data can be reproduced.
        """
        true_candidate = 0

        with pm.Model() as model:
            beta = pm.Normal(
                "beta",
                mu=0.5,
                sigma=2.0,
                shape=features_list.shape[1]
            )

            logits = features_list @ beta

            p = pm.Deterministic(
                "p",
                pm.math.softmax(logits)
            )

            y = pm.Categorical(
                "y",
                p=p,
                observed=true_candidate
            )

            # Four scalar betas and one categorical observation: a few hundred draws settle the
            # posterior mean, and the check runs off the proxy's request path but still has to
            # finish while the exchange is on the dashboard. Tunable per deployment.
            trace = pm.sample(
                draws=int(os.environ.get("PRIVACY_MCMC_DRAWS", "500")),
                tune=int(os.environ.get("PRIVACY_MCMC_TUNE", "500")),
                chains=2,
                cores=1,
                target_accept=0.99,
                progressbar=False,
                compute_convergence_checks=False,
            )

        return trace.posterior["p"].mean(dim=("chain", "draw")).values

    def check_privacy_risk(self, text: str, replaced_names: list[str]) -> list[dict]:
        """
        Checks if the given text contains any sensitive information based on the replaced names.

        Args:
            text (str): The text to be checked.
            replaced_names (list[str]): A list of names that have been replaced in the text.
        Returns:
            list[dict]: Every replaced name with its risk probability, as
            ``{"name": str, "risk_probability": float}`` records (JSON-serialisable),
            in the order given. The caller picks the maximum.

        Known limits: the probabilities are a softmax over the candidates, so a single name
        always scores 1.0; and the encoder truncates the text at 256 word pieces, so a long
        body is judged by its head.
        """
        # Create embeddings for the text and replaced names
        logger.info("Create embeddings for the text and the names.")
        text_embeddings = self.encoder_model.encode(text)
        name_embeddings = self.encoder_model.encode(replaced_names)

        # Calculate cosine similarities between the text and each replaced name
        logger.info("Calculate the similarity of the embeddings.")
        similarities = name_embeddings @ text_embeddings

        # Generate feature list
        logger.info("Extract features from the text and the names.")
        features_list = [self._make_features(similarities, text, name, sim) for name, sim in zip(replaced_names, similarities)]
        features_list = np.array(features_list)

        logger.info(f"Calculate the risk probabilities per name.")
        probabilities = self._calculate_probability(features_list)

        similarity_probabilities = list()
        for name, prob in zip(replaced_names, probabilities):
            similarity_probabilities.append({
                "name": name,
                "risk_probability": float(prob)
            })

        return similarity_probabilities
