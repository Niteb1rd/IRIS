namespace Iris.Core.Mining;

public static class DigitTemplates
{
    public const int Width = 14;
    public const int Height = 20;

    private static readonly string[] Encoded =
    {
        "0:ERITFRkdKCokHRYPExYSExk1U3B7fntdPBcSExMZKWeez9nd3a1zLRgPFS5Zrtnn6Oru3rdsNA8aSYzT5dWtqcnf1p5QFSFkuePcsVlOks3lx2sgJW3F6NagRzp5xOzZfC0mcMnmzpRANHK/6Np/MiZwyeHIjj00c7rg0nowKnDE2cCHOzNytNjJdC0scMLWvIU6NHKy1Md0LSxwwtW7hDo1dLLTx3QuLXDA1LuGOzZ2stLFcSosbLrRvo1IRIG2z71qJCVjr87Enmdnnb/Jrl0aJFCMwMu7o6S+yLmEQhEjPGKfvcbHyMm6llQoCyMoNF6Grba1qX1QJBYRHh4hM0hdY2JbPyYVExMYGBgZGRkaGhkRDRASFQ==",
        "1:DQ4QFhsfHx8fFxIPEBEOHDJRZnN2bFUyGA4PEBAxYJq+1NnEk1AgDg4QEj58vuDt79WfViMQEBIVM1+Tutfr2Z9XIxEUGRYgMUt5suHanlckERUbERMYLmGk2tidViMQERQTExYnWZ7W1JlTIQ8QFBQWGitamtDOlFAgERMYFhgeM1yRxcWQUCMUGR8YGh80WYq7vIxPJBYcIx4eIjZbjLu7ik0jFx0jHh4iN1yMurmKTiQaHyQXGiI6X4u0tIpSKx0eIRAdMU1ukrS0k2Y+HhcXFDFXfZmvvr6vlHBCJhQcP26atMDCwr+xk10zFh06Y46kra+wr6CEVjEYFiU7T1ldW1tdVUYwHhIPEhYWFhYQDxMTERAODQ==",
        "2:CwoJCw8UFRQRDg0QDQsKDRUyT2pubGRNNBwQCQoVLGugz9PQxJ1vOBkHEy1as9zk4N7e2r+AOAIaUJjZ696uobjg5bRQAR9y1enhxGFHd8r02mQBF2fI5MN7NzBkvvLmbgcWRoGZfUIlNnXD8OZvCRkXFyIhGylWoNTt3WoJFhkocZSeo7fY6eGyWBESKVOv2uTe3+bVsnQ6Eg1Il9zz69nOzJphIhELEWPI7uCxc1ZbSTQaEg8Wduj404w3EBgaGhYUExx/8vjOhjgUGRsaFBUWGXzv+OCyiHmGgXZjNxMYeOr479rOztrTw6hVERl45/P07u3r5ubeyWURFUR9hIR/gYOFhX9wOw8QFh0gHRYaISsqJRsTDQ==",
        "3:GxoaGhoaGhYPEhYdHh4VFRs+Xnp+gIBvVC4fFw8TJXKy5+/2/tmjVSoRESZQtOX0+fv77ti3XhYXQ4TY8+m2pbXb8OeAJx9fsubqy1UuV7f2+5Y+FkqPzcCDPS9asu74mkYSMVuFgF9da4jD6eqKNhMXHB8zVqTP1ePl02wWGBkaH0B1vuLj5tWkUREaGhsiPWeky97j1KlXFhkbHigrLVaKx9ni4XwkG0BxkYVdRFKHxevsiTQcWqnTw4xBMmK37e+NOB1iuNnVtk8zYbDk6oQrGk+Xz+DVpZanyNvTcBoXOWy02OXl4tzXxqJRDxYiO36y3Ofp48ybRCAOGx0lSGN3enp3Z0ocEhAfHBkiIh4WExQNCQkOEg==",
        "4:DAsLEA8JDBUnJB4UExMNDQ0PDxErTHNyXCkYEBAREQ8SHFiV0c+nSSEMFhcZFh4wicr08clsMQwbGxwaNWS05vn3z2wxDB0cHCJXp9by+/nOZSwLHBkcRobS5PL8+M5oLwsWGyppqeXX2+/ux2QuDRooRZPF4rm44OTAYy4NIkd6r72wjJjU2bplLw0tYKC+tZN7j87RtGs0Dzx2usa3mput0c+5h0gZTYG/yca7vMTS0cOkYCpNdqa1uLS3wdHRw6JfKjRIY3N0a3qbzc60dDoQJS05QkA6WIjLza5gLAkdHiAjIiFFfMbIplIkCR4cGh4eHjtssrSURR0GFxcYGhkXJ0NpaFUoFQsRExYXFREXHykmHhAPEA==",
        "5:ERMUFxcWICAXGx4eFxESKkhUWFdeXVVXV1QxExVOlKmwr7KwqqmnoVYWHHTc7/Tx8O7q6eXddRwffOr38uHKwcW9tKhbGSaC7vnitXheZ11VUTIYJ4bz+dORPxcXEhAWFRQYd+Xw2rB9YV5HMRsTDxht0d/czbuzs594NR4TH12owcbAwcTLybB0PRYrSnKMkIiIk6e1tJ5QEC40PEVEPDdMe56yrVcNK0FbZFc8KTlum7SxVwojW52lkmw8QHiju7VXBiVotburjVlaka67tFYFKVqXsrWpj5GuurabSQYqRWybsLS0trq4pXU2Bik0R3GRqa+xsJZsLBcPIiYtP09fYmJgUDgTEhYbGxoWFRUTEQ8QEhEXHA==",
        "6:FBIRERYdJCgoKysmGhAPEx0/XHV6fX2AeWU5FQsaN4G23N7f3t/TsFwZETlzwOf18O3q6t6+XhAYWKre6NSok5SWjXI8ER9uzuzXoEYcHyIfExMUGWzR7M+PSygmIRoTFBYVa9Ho1quFb2lcRyYaFBdpzN/g1c/LybePRiUTG2rI2NrUycbMzbNwNg0gasHQxquNjKvHxJFHDiNsws2weTk7fbfNqlURKm/CzK51MTWAuM+1XBMrbbzLrnYwNoW5zbRcEyNgq8GvgkJEiLbGpVMQHVOWuL2tj46rwryIQQobRX2pwcvFw8XFqWMuChgpRXmgvL27tqF3KxYPGR8qRVttbmphVT8XEhMbHB0fIycnIhgYFhATFg==",
        "7:HBgUFxYRDQwPEhUXFRIZIjJVYWBiYV1fX106HBg0X6G8vsbGvLy6tGstJFea0OPg6+/t6+rrlkwlR3OXpKKms8rh8fOaTSEhIjI5OzZOhcHl4Ig8GhsdHh8iI0qY0ubCaSEYFxcXFxgjWrzi4KNQEhoVERkbGjN57PHShD0LGhcVGBodWJ7v47diLg4YFxYZGh94ve/Tmz8dDRITFhscIJPX7MB9GwoJFBMSEyA5r+vtrGUZCAYUExIQKlnF9emPQxMKCRETFRQ9gtPy32UTCQ4UFhQSD0KW2uW3TQkJDhIXEw4OSKTf2pc9BwoNDg8NDhhUrN3SizkICwsLEg8METVqjYVSJAgKDA4WEQoJFy1FQB8RCQkNEg==",
        "8:EA4MER8wNzUpHRUSEhMSHTFdeo2Qj4dyUyYXERM0aLng7evs8NOeRiEPElqz3uHNxNDx6cV3OQ4Xac7q0JZ7kNbp251MDh5uz+q5WyxKs9/mt1kPIm7M67xgN1W7396oUAwfXrDhzZB7j87cyYQ+ChdEhdLo3eTo6dirVSYLGlSh1+PW1Nnk1qtTJw0cYbbZ0KyeqtDTtWYxDh9pwtetXUFcr8/KkEUMKnLI2qZJLE+z0tCiUhQsdMrcrFU3WrzV0KFSFSFrxd3Ch2uBydnHhT8MIl2p1dfAtb7a2LVjLQojSX210Nnb2dO+kUAeDB4pPGqUubasmndPIBQRHB8kNU1oY1tOOykWExQbGxoSGCUfGRMSERIUFQ==",
        "9:ExgdHSUwNSYCExwSEBAKGDBee42OgWZUOxYPDQMkWK/f9fDm16ZsJRELFWG/4vX88uvn47pXJwsffu721JqOpuL13otCDyWJ/fmfEwdH0/HsulwTKov8/KETCkXG6/DIXgcriPT3nxULRsXq7sZdCCqB5u6eHBFLzOvouFoQHnnj7NGimbDp9+m0WRESWLDW5ujk5/P667dYDgcgTKvc7fHw6/ftv1kHDxkpRVxtZ4rW7OKrUw4OEhULEB8WT8zo3Z9NDgEJEQ8UHRtX0u7jokgEJFSLipKdnbTk89VxMgk8i+Xl7Pn58+jZqD4dEDWH5uPn8O3iz4ZFFRIXIE1/e3p6e3JhPiEPERcPGCIdFxIVDwELEhATFQ==",
        "0:CwoKCQoLExURDQoKCgsQFyI4T2dtcG5eSSwbEBcuUHuctLO1u66SYzcYHEaAtMjGt7nN08acVh8iYK3M08mUkL3T2styJyhxx9vWvmdfpM3l4oQyK3jT4dW0TUKUy+vrjDoqedXi1LBMQ5XM7O2OOyt51uPUr0xDlszs7Y47LHrW49SuTESWzOzsjTste9fk1K9MRJbM7OyNOy581+TVr0xEl8zs7I07MH3X5NWxTkaazuzsjDo0fNPi17pbVKXU7eqJNi9ywtvdzo6JwOHw44AsKmCl0eHey8zk7ebGaRwpSHe62uLk5ujjxH0/FCo4T3mXrsPCrI9qOiYcISYuPU5fcnFaQSsaGx4WFhYWFhYZFxAODxMWGQ==",
        "1:BwgJCQoLDhARDw0NCwouIxgeOmF1gYNwThQSGVRALEV9xdnk5sqMHB8xY007a6nq6+vp0ZMaMVVnTzhVhsHe7OzUlBg6amlQNDhTfsLo7teXGEB1a1IzJSQvn9/v2poaPnBpUDEkISqe4O/ZmRk6aWRLLSIgKp7f7tiYGTxtXUUnISAqnt/t2JgZPXBSOR0eISyf4O7ZmBk9b0QxGyAkLqHi8NqZGT5xOSscIiYvo+Ty25oaP3M1Kh0kJy+k5fPcmho9b0YzHiMmL6Tm9NyaGTppWUElJCUupOb13ZoYMFdrUTInJC2k5/bemxgmRGlQMSUiLKDj9tWRFx41STglIB8kYYSOdk8THjAjHRYaHB0ZFRMMCQ8iMg==",
        "2:BwYGBgYGCAgHBwcJCQgMEhofOV5hY2RGLSIUChUoQ2OMuKuuv6B8VSsMFjRirc/VtrbV28aJQQ0WUZ3E09CUltfg17dWBxhUnr/Ft2VryN7l2mcGGlWeurypTlGw2OneaAQaQ3ien4ZVaMLg5cldAxwrQFZYTleE1unco0wJHSMsMzhBf7jq7Mt2OhMcIywyR2iy3uvVpE0uHxsiLD5noNHn46xwMCcpGyEvWY/K5uXJdTgiKzYbKUN/uO3u0ZZWLCk1QBtAdbHX7+7FdlNDTkpFGWC31ufv7tShlpahc0oXaMrm8O/z8Oft8/eXRB1nwdzl4+fn4+jr6449HkRygYJ7fX16f4GATyUbHR8cEwYHBwUJCwoLDA==",
        "3:CQoKCAgLDAoHCAoODg0LL1xxf4eGg3yAgoNBBwtJl7vO1tzXyNTb2GUCC0iUtsTF2dW50N7YZAAJHkB4g3awxrfP0rFQARARFSk2RKTMvc+4Wy4WDgwKDSxfvNzAs4MaGywGBwsTVbnW2L5+RRgnOwkKDRRcydjVwK2BLCw4CQoNDj6Ip7i8z8WMUScMDg8NEyA/cbPS39VsEhIZIRoXFiFYutrq520EFEuHeHmBRVi64PXxcAIkd9bQ0c9TSbDe9/NxACp1zNLX1W5is9/28G8AJ2CnytrdvrbE5e/SYQIqPGC43+fy7dnu0WAqDC4zQXegwdbMopp4KBwdMjI2SV92j4ZZTj0iJSw1NTY2NjlLSC4yNTY2Ng==",
        "4:BgYGBQcJCwwNDAoHCQwKDRMoSnJxXjofDxETFRUaKl6Y09CmUygUHyIjJDNQj7zd1p83GhImLC8pQmuv1OPTkh8bJDpBRDRVh8jk58J9GiQ2SE1PQ2uh0+Xgp2ssPk1QUVJOfrrV2s2EaHeSkGFSTFSN0NzVwHNxu828fVU8Y57i49O1c4To79mdWSaBsenm0K6AlOnw3qxWEqTE6eXcz8PN6+/q13got8/r5+bo8fTv8fLxjDWoyO3n5+n09vL18ueBKWN/nZOMiZ/B7vXmulwRL0FTSUREa6Ps9NqSRAsnMj46PUZyqevy1oZCEywzOzg7RGui6O3Pf0AVLDAzMDM5TmyRjnhIKxkrKysoKi41NC0jFwsUHg==",
        "5:Dg8QEBESFBQREhIQDQsRMFp7iYuKiYeJgWk0ChVQnNDl5+Xj4+XWrlIIHV+y2+3u7Oro6dmvUAQgYrXd49CPb29xa1gtCidqveXlxVAaIysuKSMfK2y+5ubFTRovNjcvLzAjZLbb38uKepuFYzIpKB9jt9rk3s3M2s2oYjcdIma73evs5uXo6tSZSxEoaLjg7ejJyejt4LhXCSxXkL6+nmVwv+Dp0GYOK0Bfi4JXLECV0vHjdRcuTnurq4pLUZ7X9Oh4GSpbntXm2YuDwuby224SIlKV3fv70Mjj8uzGXQgcOm3S+/38+/r44alNBR0sSoq74Onp4sWUSCQQHSQxSmeGkpCAZUMZFhkcHyIiJCgtJhMNDBIaIA==",
        "6:CQkKCQ8ZKi0iEggICAcKIT1QYXF1eHloUTAYCA4+fafBz7q81cmqcTQIEk2b1Onltbbo6NeuUgkXYb3k8emPjOHs5cljDh5v0evw43hy0eTgvmATI3PS5+bVf3W3zceWSxIebs7j5ty7uNPKq246Fh9w0OTo4tbX5tSzfUIXH3LU6OzmyMno59i1WxIhdNjt8+qVkt/s69lqDCV32vD05mFb0ev17nUOJnja8PLgUUnH6Pn3eg8iddjw8t9YUMfo+fh7DyBty+z16X131+7593gLH1ml4PbxuLXp9PPibAgbO3DK8PPx8vT13qNMCiEsRZLD4ebm38ucQSMXJCcxV3SKlZF8aEsdHiUjJCYoKi07MxMMDBUkMA==",
        "7:CAcGCgsLCw0QDgwNDAsIIkdxgoODg4GEgXZJJAo8f7/a39za2d/e0300D0iTxt/n2tjh6e3thSoLQorBy7eNjrnd7+h2FAsvX4R7Vjxct9/nw1wIDBssLSMUKmvX7d2XRQgLDQ8NEyBVmez20m8yCwsMDw8XKHa58vfJUSYSCwwPERosh8r08748IBsKDA4RHjSPz/TbmyQZIQoMDhElR6Xf9rBkGBomDAwNETJovu33izgUHiwUEA0TSp/W8/V/KxcgKy8fDh1ixubr12khGyMsMiAQLnjY7NicShkeJSsdFBJFkOTwyXA0FB4lKRMPEkOI1dutSyYWIicqFBARLVWCf2IsHRkkKCkcFg8QFRsQDhYZHSImKQ==",
        "8:CgcEBggMJTEyGQgGCAsNDxY4VGt7hIRjRCwYCw8cNXSctL7Dwq2NXS0KFTBdqcfJ0tXR2sqQQgcfSIG8z8ixp6vP1qtQBy9hos/TvHRmk83iulYFOWmn1dm/bmKb1OfAWAQ4XI/O39K3ts/n5r1YBjVSf8jk4uXp7vXnt1kOK0p6wuDj7vLx9d6dURghRXrB3N7h4uDs3KBUGjlYh8vf1bGnud3juVwSSm+i1Nm9YlaX0+jCXAk9bKrY3MFuYpzW6sVdCSpbnNXi0aWhxubqwlsIIEqGzObj2tzq8uS1VgoeQXW01eLs8O7nyIJCFSA2U22RusfFsp96Ni0vJTA8P1NygH1pVj8fL0EtMTYwLSw3OTQtKy87RQ==",
        "9:CQoKBRAlNjozHQ0ICw0JChE3UmaAgmpVORIMDAkTLX6mtMbEraJ8KhMMCTFtu93j4NzW27xgLAsIY87a4+e0rtbi3sJaBApz6+bo5nNu1ujs4WkDDXbt6eniVVLb7/TucAURd+vp6eNMS9/0+fNzBB533+To5mFg4vL28HACIHDP3untmZfo8vPubgEdUZfQ7PPa2O/29u9wBBgwV5vN8PTy7PX48HEFICg6cK3q2tnl8/jxcgUlVJHB3umFguHz+vRzBCJqwt3s7Xp45vb58XEDFWPB3u7zp6Ts9/TjaQIEOYPQ9P759fP47clcAgEcSp/X+vr27eG1VikQBxQsY4ihoZuQhGAWFB0SGyUoKikoIBIMCw8cJg==",
        "0:CwoKCgwQFBURDQsKCwsMDhY3WXuEhH1ePBgPDQ4ePHurztXW0LCBPyAOEj17wd/i3d3i4cWAPg8YXrTa49efnM7g3LheFSNz0+jdu19bsNnp1nMfKX3g7timQT+h1e/jfCUqfuDu1qJBQaHV8OV9Jit+4O7VoUFBodXv5H0lK37g7tWgQUGi1e/kfSUrfuDu1aBBQaLV7+N9JSx/4e7VoEFCo9bv5HwlLYDi79WgQUKk1+/kfSUsf+Hu1qRERaja8eN7IyR01enevG9wwOTw2XIcGWC54evhwMLl8ee8XhEVQ4XO7vLv7/Tvy3c2ChgrSoSuzNbTxKBwMRoQFxslP1t3g39qSCkPEBUSExUUFxsfHBELCQwRFA==",
        "1:CgkJCAoMDxITEQ8ODAopHRIcNlpzgYRiPRUWHEYyIkZ6tdDf4admHyk8Uj81fbzz9PX0uXQnQmVVQTRyrePt8/a5dCZLdlY/KkRpl8np9blzIkJpVT4hHC1PqN/0uXIfN1dUPB8ZKUyn3/O4cR40U1E6HRgpTKfe87dwHS9LTzcbGClMpt7yt28cK0NMNRoYKkyn3/O3bxwtR0gzGhgrTajg9LhwHjJQRjIbGSxPquH1uXEfNlZGMxwaLU+r4va5ch86XUk1HRstUKvj97pyH0JrTjkfGyxPrOT4u3IeRHBSPCEbLE+s5Pm7cRw9Zkk1HhonQ5fN5adiGjVWMCUYFx4sW3qJYToWME0VFBITFRcaGhkSDxIvSA==",
        "2:CAgICQwQEhIOCwkKCgoJDBUzVXmCf3BLKhIMCwscOn6x2N7b0KJrKhYODTp70fLz6uv06bpVKRITXrzo8eGytu301oZCExtv1O7huml24PLiok0NIW/N48iOQ13b8eOoTgkaSoWbhFJAceb03ZdFCBUhLzYxKFue8PTOcDEJFRYZGyMykdL16LJDHQwVFRYaOWzB7/XEfyIRERUVGCZkvOLw5os/EhEWFBYfUprs896vUhUQFRoUHTeY1fftuFcoDxQZHBM7eMvw9c+TQTUvLiMaFGC+5/f1yqWHiYV2Qhgactvw+fnz7+3t5dFrFhhpyuPr6ebk4uPZvl8REkB3iIyIhoWEhX5rOA8MDxIUExAPDw8PDw4QEQ==",
        "3:BggLDQ4PDxAPEBAPDQsHKll9jIyMi4uLgGMyDAlKntjv7+7t7e3arFILDFOu4vb29/j5+em+WgkOLll8i4+63fj32pVECA4WIS0/Wavg+Oa1WCgLDxASF0CAzvb3wHkgEhIODxQqcdLr9OuTRhMSGQwNFDF+4+/y7LNzKxsYDA0RJF2txNjr4bhfLxMNDhATJUFTgs7l1pBEDQ4SFhkbHSBUuuDhrVILEi9TZl9GKk+14Oe8WgsWYLrdzpxJUrfh6L5bChRjxOvmw2lrx+jmtVUIEFey5vPjra3j8d6cRwcMP4fX+Pv29vr0y2wuBwsfQo3B5Orp4bqBMBYLCxAcQmaGkpCBXjkTDxAKCw0OExwnJRUMCAsQFA==",
        "4:CQgICAkLDAwLCAcHBwgJCQoQMmV8emAvDggJCgkKDh9gvdjPok0UCwsNCQ0XOIbr69CYQw4QEhMPEyFapPTqvW0vDBUYGBIZLn3A+eWrSSANFhobFCA+m9b42pUrGRQaGxsSK1i76fC5eS9DT0cwHhE6ec/u5ZxvX4yhiVAiFFCg3OvYgmuTyNq3ZiYdacXo5cd1a6rW4LdpKjN/2+/t3Le22evs0pFdRI3j8vXw7O/3+fXpsoNDhtXn7ero6/X59+6veSVGbnh7eXyYzOju04NCEBwpLi4tNGK14Oi/ZyEJDxYYGBkjVrTg57dfGgsQFhYXGCFRptbdqFcYDRAUFRUWHDZmgoVhMxAPEBMTExQVGBsbGA8LCQ==",
        "5:CAoOEhMSExMTExEOCwkJLl19iYmIiImIeVQpCgxQp9Xn5+Xl5eXNkEMJEGHF7Pn38O3u7taZRggSY8ft7tadgYGDdE0mChRkx+3ku1koKCwpHxcSFWTG69+xRhIVFxcWFxcUY8To4Lt5XGRSOx4XFhRiwubq3NPS2cGSQSERFWTE5/Lu5+jx7MhzNw4WYb7k6diur93t3ZxLDxZKjLKsik9fueLntlsTFipGX1c7IUWm2+vFZBYWQnuln3g6TKze7MhmFhNUqNzfwnV3yOnqvF0RDUue3/Trv7/r9eGgSwoILmnH8vr29vv0ymktBwgXNHOp1+Df1KZuKxQJCQ0XMFJ4goBzSygOCwwKCw4NDxARDwoHBwkMDg==",
        "6:CwsLDA8UGxsUDgoKCgoKDRY4XICLinxTLRELCwobOoG12+Ti16hvKRQLCTl90PT59fT37L1XJwwOWLbn9Om+v+vz14c/DBVozu/qyXR30erdmkoOGm7V8OGzV1SqxbyBPxAZbdLt5MOTh5+PcDwhEhZq0Orv5+De4cKNOh8VFGnP6vLu4eDt5r9mMxQUadDs7dugoNnq2JRJEhZr0e7kvlpbwOTkslkTF2vR7t+vSUy44enCYRMWac7u4LFKTbvj68ZjExFhxOvpx29xzuvpvV0QC1Gq5PTntrfq9OGjTQsINHXP9vv19frzym0wCAobO4G23uTi16x1LxcNDBEbOl6Diod5Ui4QDxINDxESFBcaFxALCQsRFg==",
        "7:CgwOEBEREhISEhEPDAoPNmd/iIiHh4iIgGk5EhVdt9vp6Obn6encuWIdGmrM6fLv7e7z9enJcSkZZsThzJmMpOHw5bZiHxhLiaCCSEx+3/DdmE0VFSEwMykZR47s89FyNAsUGBwcGx1or/Luvk8iDBQVFxgbJYbL9eGmMhYOFBQWFyAynt/2zoofDxEUFBUXKkyz7Pa0aRUPFRMUFRc6csbx8pZFEBAZExQVGEub1/HpeCUNExwTExUcXcHm7dVhFQ4WHhUUFiZx2+/grkoNDxgfFRMYPIrt9NGCNQkQGiERERxbp/f1wFcjCBIcJA4QHmeu8eGiMhYLEx8nCw4YRG2Sg1oYDgwSHyoKDA8UGBwXEAgKDRAfKw==",
        "8:EQ4KCg0RFxcSDgwLCwsMDxc3V3Z9fXhaORQMCwcaOXikw8jJxal7NBkLBTN0uNTV0tPW17twNAoJVbTY5d+npdrl27RUBg1m0Ofo1W1t1Orq0GIGD2rW7OjOWlvR6+3VZQcPYsjo7uCOjd7r479aBw5Mntvy8ODg8fPWikIPDj2A0PH09fb29ctiMxoOSJXY8vLh4fL01H5GIQ9fwubx56Wk5PLnvGMdEWvX7erTYWLW7u/UahISbtzv6M1WWdXu8dlpCxFp0+zu231+3vHu0GMIDlq55PPtx8fs9eWyUwkMPIDQ8vX09PbyymoyEQ0mToy52ODe0ap1LSEhEh4xT2yJlI54VDMYIS0bICYpLDA2LxoUFR4qMw==",
        "9:CQkJCg8VGRgUDgsKCgsJCxVFa4mQjH5VLxAKCgkfRJPG5erm2apvJREKC0mb2PP28/L27btMIAkUZcnq8OKws+vz1oM8Ch913PHftGFu2/Djp1ANJ33i8tGOOE7R7ei6WxAofODxz403TtDr6L5eECF01+zXplBg1evlu1sNGGTA5ObToafk7uK4WAsVRYnO7PDn5/Hz47hZDBUpTaHS6ubp8PTmulsQFi9VjKWoi53c7+i9XREXWKrOxqBRY9Tt6b9eDxNkxuroy3h+3/HmtVcMC1e25vXuwcLx9tyVRAgFNnzR9vv4+PvwwFUjBwUZPIrA5Onk16htJBELBgwaQmeJjol5UCoODA8HCw8RFRkbGRELCQsOEQ==",
    };

    private static readonly float[][][] Templates = Enumerable.Range(0, 10)
        .Select(d => Encoded.Where(e => e[0] == (char)('0' + d)).Select(e => Decode(e[2..])).ToArray())
        .ToArray();

    private static float[] Decode(string text)
    {
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length != Width * Height)
            throw new InvalidOperationException("A mining digit template has the wrong size.");
        return bytes.Select(b => b / 255f).ToArray();
    }

    public static (char Digit, double Score) Match(float[] patch, double tieMargin, out double[] allScores)
    {
        var scores = new double[10];
        allScores = scores;
        var closest = new float[10][];
        for (var d = 0; d < 10; d++)
        {
            scores[d] = double.NegativeInfinity;
            foreach (var template in Templates[d])
            {
                var score = ShiftedCorrelation(patch, template);
                if (score > scores[d])
                {
                    scores[d] = score;
                    closest[d] = template;
                }
            }
        }

        var order = Enumerable.Range(0, 10).OrderByDescending(d => scores[d]).ThenBy(d => d).ToArray();
        var best = order[0];
        var second = order[1];
        if (scores[best] - scores[second] < tieMargin && PrefersSecond(patch, closest[best], closest[second]))
            (best, second) = (second, best);
        return ((char)('0' + best), scores[best]);
    }

    private static double Correlation(float[] a, float[] b, int x0a, int y0a, int x0b, int y0b, int w, int h)
    {
        double ma = 0, mb = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                ma += a[(y0a + y) * Width + x0a + x];
                mb += b[(y0b + y) * Width + x0b + x];
            }

        var n = (double)(w * h);
        ma /= n;
        mb /= n;
        double ab = 0, aa = 0, bb = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var da = a[(y0a + y) * Width + x0a + x] - ma;
                var db = b[(y0b + y) * Width + x0b + x] - mb;
                ab += da * db;
                aa += da * da;
                bb += db * db;
            }

        return ab / Math.Sqrt(aa * bb + 1e-9);
    }

    private static double ShiftedCorrelation(float[] a, float[] b)
    {
        var best = -1.0;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                best = Math.Max(best, Correlation(a, b, Math.Max(0, dx), Math.Max(0, dy), Math.Max(0, -dx), Math.Max(0, -dy), Width - Math.Abs(dx), Height - Math.Abs(dy)));
        return best;
    }

    private static float[] Shifted(float[] t, int dx, int dy)
    {
        var q = new float[t.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                int sx = x - dx, sy = y - dy;
                if (sx >= 0 && sx < Width && sy >= 0 && sy < Height)
                    q[y * Width + x] = t[sy * Width + sx];
            }

        return q;
    }

    private static float[] BestShift(float[] p, float[] t)
    {
        float[]? best = null;
        var bestScore = double.NegativeInfinity;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var q = Shifted(t, dx, dy);
                var score = Correlation(p, q, 0, 0, 0, 0, Width, Height);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = q;
                }
            }

        return best!;
    }

    private static double[] Standardised(float[] v)
    {
        var mean = v.Average(x => (double)x);
        var sd = Math.Sqrt(v.Average(x => (x - mean) * (x - mean)));
        return v.Select(x => (x - mean) / (sd + 1e-6)).ToArray();
    }

    private static bool PrefersSecond(float[] patch, float[] a, float[] b)
    {
        var weights = new double[a.Length];
        var total = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            weights[i] = Math.Abs(a[i] - b[i]);
            total += weights[i];
        }

        var p = Standardised(patch);
        double Error(float[] t)
        {
            var q = Standardised(BestShift(patch, t));
            var e = 0.0;
            for (var i = 0; i < q.Length; i++)
                e += weights[i] / (total + 1e-9) * (p[i] - q[i]) * (p[i] - q[i]);
            return e;
        }

        return Error(a) > Error(b);
    }
}
